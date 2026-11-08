using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xbank.DevAgent.Api;

namespace Xbank.DevAgent.Tests;

public sealed class DeveloperSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "developer-settings-tests", Guid.NewGuid().ToString("N"));
    private string Profile => Path.Combine(_directory, "settings.protected");
    private readonly IDataProtector _protector = new EphemeralDataProtectionProvider().CreateProtector("tests");

    private (DeveloperSettingsStore Store, IConfigurationRoot Configuration) Create()
    {
        DeveloperSettingsStore store = new(Profile, _protector);
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Projects:legacy:Key"] = "legacy",
            ["Projects:legacy:RepoPath"] = _directory,
            ["Projects:legacy:GitLabProjectPath"] = "https://gitlab.test/team/legacy.git",
            ["GitLab:Token"] = "legacy-token",
        }).Add(store).Build();
        return (store, configuration);
    }

    private DeveloperSettings Settings() => new("gitlab.test", "https://jira.test", "", "Bearer",
        [new("first", _directory, "https://gitlab.test/team/first.git", "repo-secret")],
        GitLabToken: "shared-secret", JiraToken: "jira-secret");

    [Fact]
    public void SavesEncryptedProfileAndReloadsWithoutDisclosingTokens()
    {
        var (store, configuration) = Create();
        store.Save(Settings(), configuration);
        string disk = File.ReadAllText(Profile);
        Assert.DoesNotContain("repo-secret", disk);
        Assert.DoesNotContain("jira-secret", disk);
        var (reloaded, reloadedConfiguration) = Create();
        DeveloperSettings view = reloaded.Read(reloadedConfiguration);
        Assert.True(view.HasGitLabToken);
        Assert.True(view.HasJiraToken);
        Assert.Null(view.GitLabToken);
        Assert.Null(view.JiraToken);
        Assert.True(Assert.Single(view.Repositories).HasToken);
        Assert.Null(view.Repositories[0].Token);
        Assert.Equal("jira-secret", reloadedConfiguration["jira:password"]);
        Assert.Equal("repo-secret", DeveloperSettingsStore.GitLabToken(reloadedConfiguration, "https://gitlab.test/team/first"));
        Assert.Equal("shared-secret", DeveloperSettingsStore.GitLabToken(reloadedConfiguration, "https://gitlab.test/team/other.git"));
    }

    [Fact]
    public void BlankInputsPreserveTokensAndExplicitClearsRemoveThem()
    {
        var (store, configuration) = Create();
        store.Save(Settings(), configuration);
        store.Save(store.Read(configuration), configuration);
        Assert.Equal("repo-secret", DeveloperSettingsStore.GitLabToken(configuration, "https://gitlab.test/team/first.git"));
        Assert.Equal("jira-secret", configuration["Jira:Password"]);
        store.Save(store.Read(configuration) with
        {
            GitLabToken = "", JiraToken = "",
            Repositories = [Settings().Repositories[0] with { Token = "" }],
        }, configuration);
        Assert.False(store.Read(configuration).HasGitLabToken);
        Assert.False(store.Read(configuration).HasJiraToken);
        Assert.Equal("", DeveloperSettingsStore.GitLabToken(configuration, "https://gitlab.test/team/first.git"));
    }

    [Fact]
    public void SavedRepositoriesReplaceDefaultsAndCanBeRemovedEntirely()
    {
        var (store, configuration) = Create();
        Assert.Equal("legacy", Assert.Single(DeveloperSettingsStore.Projects(configuration))["Key"]);
        store.Save(Settings(), configuration);
        Assert.Equal("first", Assert.Single(DeveloperSettingsStore.Projects(configuration))["Key"]);
        store.Save(Settings() with { Repositories = [] }, configuration);
        Assert.Empty(DeveloperSettingsStore.Projects(configuration));
        var (reloaded, reloadedConfiguration) = Create();
        Assert.Empty(reloaded.Read(reloadedConfiguration).Repositories);
    }

    [Fact]
    public void RepositoryTokenDoesNotFollowAnEditedRemoteUrl()
    {
        var (store, configuration) = Create();
        store.Save(Settings(), configuration);
        store.Save(store.Read(configuration) with
        {
            Repositories = [Settings().Repositories[0] with { GitLabProjectPath = "https://gitlab.test/team/other.git", Token = null }],
        }, configuration);
        Assert.False(Assert.Single(store.Read(configuration).Repositories).HasToken);
    }

    [Theory]
    [InlineData("http://gitlab.test/team/repo")]
    [InlineData("https://secret@gitlab.test/team/repo")]
    [InlineData("https://gitlab.test/team/repo?token=secret")]
    public void RejectsUnsafeRepositoryUrlsWithoutWritingProfile(string url)
    {
        var (store, configuration) = Create();
        Assert.Throws<ArgumentException>(() => store.Save(Settings() with
        {
            Repositories = [new("first", _directory, url)],
        }, configuration));
        Assert.False(File.Exists(Profile));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
