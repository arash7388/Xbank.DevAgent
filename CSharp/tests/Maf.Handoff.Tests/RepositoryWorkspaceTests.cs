using Maf.Handoff.Core;
using System.Reflection;

namespace Maf.Handoff.Tests;

public sealed class RepositoryWorkspaceTests
{
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
