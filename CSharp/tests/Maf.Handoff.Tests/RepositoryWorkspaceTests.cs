using Maf.Handoff.Core;

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
}

