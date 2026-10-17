using System.Diagnostics;
using System.Reflection;
using Maf.Handoff.Core;

namespace Maf.Handoff.Tests;

public sealed class RepositoryBuildTests
{
    [Theory]
    [InlineData("error NU1101: Unable to find package XBank.OAuth. No packages exist", "XBank.OAuth")]
    [InlineData("error NU1102: Unable to find package Example.Library with version (>= 1.0)", "Example.Library")]
    [InlineData("error NU1301: Unable to load the service index", "network access")]
    [InlineData("error NETSDK1045: The current .NET SDK does not support", ".NET SDK")]
    public void EnvironmentFailuresExplainTheRequiredAction(string diagnostic, string expected)
    {
        RepositoryBuildResult result = RepositoryBuildResult.Failed(diagnostic);
        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.BlockerMessage);
        Assert.Contains("have not been approved", result.BlockerMessage);
    }

    [Theory]
    [InlineData("error CS1002: ; expected")]
    [InlineData("error NU1605: Detected package downgrade")]
    [InlineData("An arbitrary build failure")]
    public void CodeFailuresRemainEligibleForRemediation(string diagnostic)
        => Assert.Null(RepositoryBuildResult.Failed(diagnostic).BlockerMessage);

    [Fact]
    public async Task BuildsNewProjectReportsCompilerErrorsAndBuildsTheFix()
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-build", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using Process git = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = root, ArgumentList = { "init", "--quiet" },
                UseShellExecute = false, CreateNoWindow = true,
            })!;
            await git.WaitForExitAsync();
            Assert.Equal(0, git.ExitCode);
            File.WriteAllText(Path.Combine(root, ".gitignore"), "bin/\nobj/\n");
            File.WriteAllText(Path.Combine(root, "Example.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "Example.cs"), "public class Example { invalid syntax }");
            RepositoryWorkspace workspace = (RepositoryWorkspace)typeof(RepositoryWorkspace)
                .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
                .Invoke([root, "https://github.com/example/repo", "main", null]);

            RepositoryBuildResult failed = await workspace.BuildAsync(CancellationToken.None);
            Assert.False(failed.Succeeded);
            Assert.Contains("error CS", failed.Evidence);
            File.WriteAllText(Path.Combine(root, "Example.cs"), "public class Example { }");
            RepositoryBuildResult fixedResult = await workspace.BuildAsync(CancellationToken.None);
            Assert.True(fixedResult.Succeeded, fixedResult.Evidence);
            Assert.Contains("BUILD_PASSED", fixedResult.Evidence);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
