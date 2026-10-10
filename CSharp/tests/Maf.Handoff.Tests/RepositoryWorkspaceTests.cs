using Maf.Handoff.Core;
using System.Reflection;

namespace Maf.Handoff.Tests;

public sealed class RepositoryWorkspaceTests
{
    [Fact]
    public void LoopFingerprintDetectsEditsBeyondTheDisplayedDiffLimit()
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-loop-fingerprint", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using System.Diagnostics.Process git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = root, ArgumentList = { "init", "--quiet" },
                UseShellExecute = false, CreateNoWindow = true,
            })!;
            git.WaitForExit();
            Assert.Equal(0, git.ExitCode);
            File.WriteAllText(Path.Combine(root, "a.txt"), new string('a', 160000));
            File.WriteAllText(Path.Combine(root, "b.txt"), new string('b', 160000));
            File.WriteAllText(Path.Combine(root, "z.txt"), "first version");
            RepositoryWorkspace workspace = CreateWorkspace(root);
            string displayed = workspace.GetDiff();
            string fingerprint = workspace.GetProgressFingerprint();
            Assert.Contains("[output truncated]", displayed);
            workspace.WriteFile("z.txt", "second version");
            Assert.Equal(displayed, workspace.GetDiff());
            Assert.NotEqual(fingerprint, workspace.GetProgressFingerprint());
            workspace.WriteFile("z.txt", "first version");
            Assert.Equal(fingerprint, workspace.GetProgressFingerprint());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public void RemoteBranchListPreservesCaseAndExcludesTags()
    {
        string output = "abc\trefs/heads/develop\r\nabc\trefs/heads/Develop\nabc\trefs/tags/v1\nabc\tHEAD\nabc\trefs/heads/feature/orders\n";

        Assert.Equal(["Develop", "develop", "feature/orders"], RepositoryWorkspace.ParseRemoteBranches(output));
    }

    [Theory]
    [InlineData("develop", "Develop")]
    [InlineData("Develop", "develop")]
    [InlineData("DEVELOP", "Develop")]
    [InlineData("feature/orders", "Feature/Orders")]
    public void BaseBranchResolvesToRemoteSpelling(string requested, string actual)
    {
        Assert.Equal(actual, RepositoryWorkspace.ResolveBaseBranch(requested, ["main", actual]));
    }

    [Theory]
    [InlineData("Develop")]
    [InlineData("develop")]
    public void BaseBranchPrefersExactMatch(string requested)
    {
        Assert.Equal(requested, RepositoryWorkspace.ResolveBaseBranch(requested, ["Develop", "develop"]));
    }

    [Fact]
    public void BaseBranchRejectsAmbiguousCasing()
    {
        RepositoryException exception = Assert.Throws<RepositoryException>(() =>
            RepositoryWorkspace.ResolveBaseBranch("DEVELOP", ["Develop", "develop"]));

        Assert.Contains("Enter the exact branch name", exception.Message);
    }

    [Fact]
    public void BaseBranchRejectsMissingBranch()
    {
        RepositoryException exception = Assert.Throws<RepositoryException>(() =>
            RepositoryWorkspace.ResolveBaseBranch("develop", ["main", "feature/develop"]));

        Assert.Contains("was not found", exception.Message);
    }

    [Fact]
    public void CompactReadMinifiesJsonWithoutChangingItsStructure()
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-workspace-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "settings.json"), """
                {
                  "enabled": true,
                  "items": [1, 2, 3]
                }
                """);
            RepositoryWorkspace workspace = CreateWorkspace(root);

            Assert.Equal(
                "{\"enabled\":true,\"items\":[1,2,3]}",
                workspace.ReadFileCompact("settings.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompactReadPreservesIndentationAndCommentsWhileRemovingWastefulWhitespace()
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-workspace-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "example.py"),
                "def run():   \r\n    # explanation  \r\n\r\n\r\n    return True  \r\n");
            RepositoryWorkspace workspace = CreateWorkspace(root);

            Assert.Equal(
                "def run():\n    # explanation\n\n    return True",
                workspace.ReadFileCompact("example.py"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../main")]
    [InlineData("feature@{1")]
    [InlineData("feature..next")]
    [InlineData("feature/")]
    public void BranchValidationRejectsUnsafeNames(string branch)
    {
        Assert.Throws<RepositoryException>(() => RepositoryWorkspace.ValidateBranchName(branch));
    }

    [Fact]
    public async Task CloneRejectsNonGitHubUrlAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-workspace-test", Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<RepositoryException>(() => RepositoryWorkspace.CloneAsync(
            "https://example.com/owner/repository",
            "main",
            githubToken: null,
            root,
            CancellationToken.None));
    }

    [Fact]
    public async Task CloneRejectsGitLabUrlFromUnconfiguredHostAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-workspace-test", Guid.NewGuid().ToString("N"));

        RepositoryException exception = await Assert.ThrowsAsync<RepositoryException>(() => RepositoryWorkspace.CloneAsync(
            "https://gitlab.example.com/group/repository.git",
            "main",
            githubToken: null,
            root,
            CancellationToken.None,
            RepositoryProvider.GitLab));

        Assert.Contains("gitlab.com", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryValidationAcceptsConfiguredSelfManagedGitLabHost()
    {
        RepositoryWorkspace.ValidateRepositoryUrl(
            "https://gitlab.dotin.ir/platform/orders.git",
            RepositoryProvider.GitLab,
            "gitlab.dotin.ir");
    }

    [Fact]
    public void RepositoryValidationStillRejectsOtherGitLabHosts()
    {
        RepositoryException exception = Assert.Throws<RepositoryException>(() =>
            RepositoryWorkspace.ValidateRepositoryUrl(
                "https://untrusted.example/platform/orders.git",
                RepositoryProvider.GitLab,
                "gitlab.dotin.ir"));

        Assert.Contains("gitlab.dotin.ir", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingSourceFilesAreRecoverableButUnsafePathsRemainRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-workspace-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RepositoryWorkspace workspace = CreateWorkspace(root);

            string read = workspace.ReadFile("Services/MissingClient.cs");
            string replace = workspace.ReplaceText("Services/MissingClient.cs", "old", "new");

            Assert.Contains("Source file does not exist", read, StringComparison.Ordinal);
            Assert.Contains("list_repository_files", read, StringComparison.Ordinal);
            Assert.Contains("no change was made", replace, StringComparison.Ordinal);
            Assert.Throws<RepositoryException>(() => workspace.ReadFile("../secrets.txt"));
            Assert.Throws<RepositoryException>(() => workspace.ReplaceText(".git/config", "old", "new"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RepositoryWorkspace CreateWorkspace(string root) =>
        (RepositoryWorkspace)(Activator.CreateInstance(
            typeof(RepositoryWorkspace),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [root, "https://github.com/example/repository", "main", null],
            culture: null) ?? throw new InvalidOperationException("Could not construct test workspace."));
}
