using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xbank.DevAgent.Api;
using Xbank.DevAgent.Core;

namespace Xbank.DevAgent.Tests;

public sealed class TaskAnalysisCodebaseTests
{
    private static IConfigurationRoot Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["GitLab:Host"] = "gitlab.test", ["GitLab:Token"] = "shared-secret",
        ["Projects:first:Key"] = "xbank.first", ["Projects:first:GitLabProjectPath"] = "https://gitlab.test/group/first.git",
        ["Projects:first:Token"] = "repo-secret",
        ["Projects:second:Key"] = "friendly-name", ["Projects:second:GitLabProjectPath"] = "https://gitlab.test/group/xbank.second.git",
    }).Build();

    private static JsonElement Issue(params string[] names) => JsonSerializer.SerializeToElement(new
    {
        fields = new { components = names.Select(name => new { name }) },
    });

    [Fact]
    public void MatchesAllComponentsByConfiguredKeyOrProjectNameAndUsesRepositoryPatFirst()
    {
        List<string> notices = [];
        var matches = new TaskAnalysisCodebaseLoader(Configuration()).Resolve(
            Issue("XBank.First", "xbank.second", "xbank.first", "unrelated", "xbank.missing"), notices);
        Assert.Equal(2, matches.Count);
        Assert.Equal("repo-secret", matches[0].Token);
        Assert.Equal("shared-secret", matches[1].Token);
        Assert.All(matches, match => Assert.Equal("gitlab.test", match.Host));
        Assert.Contains("xbank.missing", Assert.Single(notices));
    }

    [Fact]
    public void DoesNotGuessUrlsOrUseUntrustedHostsAndReportsAmbiguousMappingsOrMissingPat()
    {
        var config = Configuration();
        config["Projects:duplicate:Key"] = "xbank.first";
        config["Projects:duplicate:GitLabProjectPath"] = "https://gitlab.test/other/first.git";
        config["Projects:second:GitLabProjectPath"] = "https://other.test/group/xbank.second.git";
        config["Projects:third:Key"] = "xbank.third";
        config["Projects:third:GitLabProjectPath"] = "https://gitlab.test/group/third.git";
        config["GitLab:Token"] = "";
        List<string> notices = [];
        Assert.Empty(new TaskAnalysisCodebaseLoader(config).Resolve(Issue("xbank.first", "xbank.second", "xbank.third"), notices));
        Assert.Contains(notices, notice => notice.Contains("multiple configured"));
        Assert.Contains(notices, notice => notice.Contains("matching GitLab host"));
        Assert.Contains(notices, notice => notice.Contains("no GitLab PAT"));
    }

    [Fact]
    public async Task FetchesEveryMatchedRepositoryAndDeletesOnlyOwnedCheckoutsOnDispose()
    {
        List<TaskAnalysisRepository> fetched = [];
        var loader = new TaskAnalysisCodebaseLoader(Configuration(), (repository, root, token) =>
        {
            fetched.Add(repository);
            string path = Path.Combine(root, repository.Component);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "Source.cs"), "source");
            return Task.FromResult(RepositoryWorkspace.OpenExisting(path));
        });
        var result = await loader.LoadAsync(Issue("xbank.first", "xbank.second"), CancellationToken.None);
        string ownedRoot = result.RootPath;
        Assert.Equal(2, fetched.Count);
        Assert.Equal(2, result.Repositories.Count);
        Assert.True(Directory.Exists(ownedRoot));
        result.Dispose();
        Assert.False(Directory.Exists(ownedRoot));
    }

    [Fact]
    public async Task ReportsFetchFailureWithoutCredentialsAndContinuesOtherComponents()
    {
        var loader = new TaskAnalysisCodebaseLoader(Configuration(), (repository, root, token) =>
        {
            if (repository.Component == "xbank.first") throw new RepositoryException("repo-secret should not escape");
            string path = Path.Combine(root, "second");
            Directory.CreateDirectory(path);
            return Task.FromResult(RepositoryWorkspace.OpenExisting(path));
        });
        using var result = await loader.LoadAsync(Issue("xbank.first", "xbank.second"), CancellationToken.None);
        Assert.Equal("xbank.second", Assert.Single(result.Repositories).Key);
        Assert.DoesNotContain("repo-secret", Assert.Single(result.Notices));
        Assert.Contains("could not fetch", result.Notices[0]);
    }

    [Fact]
    public async Task CancellationStopsFetchingAndCleansPartiallyCreatedCheckout()
    {
        string? ownedRoot = null;
        using var cancelled = new CancellationTokenSource();
        var loader = new TaskAnalysisCodebaseLoader(Configuration(), (repository, root, token) =>
        {
            ownedRoot = root;
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "partial"), "source");
            cancelled.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.LoadAsync(Issue("xbank.first"), cancelled.Token));
        Assert.NotNull(ownedRoot);
        Assert.False(Directory.Exists(ownedRoot));
    }

    [Fact]
    public async Task NoComponentsDoesNotFetchAndReportsMissingCodebase()
    {
        var loader = new TaskAnalysisCodebaseLoader(Configuration(), (_, _, _) => throw new InvalidOperationException("Should not fetch"));
        using var result = await loader.LoadAsync(Issue("Other"), CancellationToken.None);
        Assert.Empty(result.Repositories);
        Assert.Contains("No xbank.*", Assert.Single(result.Notices));
    }

    [Theory]
    [InlineData("ref: refs/heads/develop\tHEAD\n123\tHEAD\n", "develop")]
    [InlineData("ref: refs/heads/release/current\tHEAD\r\n123\tHEAD\r\n", "release/current")]
    public void UsesAdvertisedDefaultBranchRatherThanAssumingMain(string output, string branch) =>
        Assert.Equal(branch, RepositoryWorkspace.ParseDefaultBranch(output));

    [Fact]
    public void RejectsMissingOrInvalidRemoteDefaultBranch()
    {
        Assert.Throws<RepositoryException>(() => RepositoryWorkspace.ParseDefaultBranch("123\tHEAD\n"));
        Assert.Throws<RepositoryException>(() => RepositoryWorkspace.ParseDefaultBranch("ref: refs/heads/../escape\tHEAD\n"));
    }
}
