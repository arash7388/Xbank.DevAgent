using System.Diagnostics;
using System.Text;
using Maf.Handoff.Core;

namespace Maf.Handoff.Tests;

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
        Assert.Equal("1", info.Environment["GIT_CONFIG_COUNT"]);
    }
}
