using System.Diagnostics;
using System.Text;
using Xbank.DevAgent.Core;

namespace Xbank.DevAgent.Tests;

public sealed class RepositoryGitAuthenticationTests
{
    [Theory]
    [InlineData(RepositoryProvider.GitLab, "oauth2")]
    [InlineData(RepositoryProvider.GitHub, "x-access-token")]
    public async Task ExplicitTokenResetsCredentialHelpersAndInheritedHeaders(
        RepositoryProvider provider, string userName)
    {
        ProcessStartInfo info = RepositoryWorkspace.CreateGitStartInfo(
            Path.GetTempPath(), ["config", "--get-all", "http.extraHeader"], provider, " test-token ");
        string configPath = Path.GetTempFileName();
        try
        {
            // Supply inherited headers without touching the user's Git configuration.
            await File.WriteAllTextAsync(configPath, "[http]\nextraHeader = Authorization: Basic stale\n");
            info.Environment["GIT_CONFIG_GLOBAL"] = configPath;
            info.Environment.Remove("GIT_CONFIG_PARAMETERS");
            using Process process = Process.Start(info)!;
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.True(process.ExitCode == 0, error);
            string expected = "Authorization: Basic " + Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{userName}:test-token"));
            // Git preserves an empty reset entry before the app's authorization header.
            Assert.EndsWith($"\n\n{expected}\n", output.Replace("\r\n", "\n"));
            Assert.Equal("credential.helper", info.Environment["GIT_CONFIG_KEY_1"]);
            Assert.Equal(string.Empty, info.Environment["GIT_CONFIG_VALUE_1"]);
            Assert.Equal("0", info.Environment["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("Never", info.Environment["GCM_INTERACTIVE"]);
            Assert.DoesNotContain("test-token", string.Join(" ", info.ArgumentList));
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public void BackgroundGitWithoutExplicitTokenAlsoDisablesInteractiveLogin()
    {
        ProcessStartInfo info = RepositoryWorkspace.CreateGitStartInfo(
            Path.GetTempPath(), ["ls-remote", "--heads"], RepositoryProvider.GitLab, null);

        Assert.Equal("0", info.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("Never", info.Environment["GCM_INTERACTIVE"]);
        Assert.Equal("2", info.Environment["GIT_CONFIG_COUNT"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("test-token")]
    public async Task ClonesLongPathsWhenGlobalConfigurationDisablesThem(string? accessToken)
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-long-paths", Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string clone = Path.Combine(root, new string('w', 60));
        string configPath = Path.Combine(root, "gitconfig");
        string relativePath = Path.Combine("XBank.Marketing", "XBank.Marketing.Infrastructure.EF.SqlMigrations",
            "Migrations", "20260721000000_AddNotifiableAndMessagePatternToScoreTransactionMetaData.Designer.cs");
        Directory.CreateDirectory(source);
        try
        {
            await File.WriteAllTextAsync(configPath, "[core]\nlongpaths = false\nautocrlf = false\n");
            await Git(source, "init", "--initial-branch=main");
            await Git(source, "config", "core.longpaths", "true");
            string sourceFile = Path.Combine(source, relativePath);
            Assert.True(Path.Combine(clone, relativePath).Length > 260);
            Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
            await File.WriteAllTextAsync(Path.Combine(source, ".gitattributes"), "* -text\n");
            await File.WriteAllTextAsync(sourceFile, "class Migration { }\n");
            await Git(source, "add", ".");
            await Git(source, "-c", "user.name=Test", "-c", "user.email=test@example.com",
                "-c", "commit.gpgSign=false", "commit", "-m", "Long path");

            await Git(root, "clone", "--", source, clone);

            string cloneFile = Path.Combine(clone, relativePath);
            Assert.Equal("class Migration { }\n", (await File.ReadAllTextAsync(cloneFile)).Replace("\r\n", "\n"));
            Assert.Equal("true", (await Git(clone, "config", "--local", "--get", "core.longpaths")).Trim());
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }

        async Task<string> Git(string directory, params string[] arguments)
        {
            ProcessStartInfo info = RepositoryWorkspace.CreateGitStartInfo(
                directory, arguments, RepositoryProvider.GitHub, accessToken);
            info.Environment["GIT_CONFIG_GLOBAL"] = configPath;
            info.Environment.Remove("GIT_CONFIG_PARAMETERS");
            using Process process = Process.Start(info)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }
    }
}
