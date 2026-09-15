using Maf.Handoff.Core;
using System.Reflection;

namespace Maf.Handoff.Tests;

public sealed class RepositoryWorkspaceTests
{
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
