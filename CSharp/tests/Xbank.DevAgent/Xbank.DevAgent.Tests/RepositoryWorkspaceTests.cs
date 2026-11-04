using Xbank.DevAgent.Core;
using System.Reflection;

namespace Xbank.DevAgent.Tests;

public sealed class RepositoryWorkspaceTests
{
    [Fact]
    public async Task CommitsReviewFixOnExistingSourceBranch()
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-review-commit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await GitAsync(root, "init", "--initial-branch=review-source");
            File.WriteAllText(Path.Combine(root, "Fix.cs"), "class Fix {}\n");
            await GitAsync(root, "add", ".");
            await GitAsync(root, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "initial");
            RepositoryWorkspace workspace = CreateWorkspace(root);
            workspace.WriteFile("Fix.cs", "class Fix { public int Value => 1; }\n");
            ApprovalResult result = await workspace.CommitAsync("review-source", "Address MR feedback", false, CancellationToken.None);
            Assert.Equal("review-source", result.Branch);
            Assert.Equal("review-source", await GitAsync(root, "branch", "--show-current"));
            Assert.Equal("Address MR feedback", await GitAsync(root, "log", "-1", "--format=%s"));
            Assert.Equal(string.Empty, await GitAsync(root, "status", "--porcelain"));
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellingGitTerminatesTheProcessAndItsChildren()
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-git-cancellation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using CancellationTokenSource cancellation = new();
            RepositoryWorkspace workspace = CreateWorkspace(root);
            MethodInfo runGit = typeof(RepositoryWorkspace).GetMethod("RunGitAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Task operation = (Task)runGit.Invoke(workspace,
                [root, new string[] { "-c", "alias.maf-wait=!echo started > maf-started; sleep 30", "maf-wait" }, cancellation.Token, true])!;
            using CancellationTokenSource setup = new(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(root, "maf-started")))
            {
                setup.Token.ThrowIfCancellationRequested();
                await Task.Delay(25, setup.Token);
            }
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("first\nsecond\n", "@@ -0,0 +1,2 @@", false)]
    [InlineData("first\r\nsecond\r\n", "@@ -0,0 +1,2 @@", false)]
    [InlineData("class Added { }", "@@ -0,0 +1,1 @@", true)]
    [InlineData("", null, false)]
    public async Task NewFileDiffContainsValidHunksAndDoesNotStageFiles(
        string content, string? expectedHunk, bool missingNewline)
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-added-diff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await GitAsync(root, "init", "--quiet");
            File.WriteAllText(Path.Combine(root, "Added.cs"), content);
            RepositoryWorkspace workspace = CreateWorkspace(root);

            string diff = workspace.GetDiff();

            Assert.Contains("new file mode 100644", diff);
            Assert.Contains("+++ b/Added.cs", diff);
            if (expectedHunk is null) Assert.DoesNotContain("@@", diff);
            else Assert.Contains(expectedHunk, diff);
            Assert.Equal(missingNewline, diff.Contains("\\ No newline at end of file"));
            Assert.Equal("?? Added.cs", await GitAsync(root, "status", "--porcelain"));
            // Git itself must be able to match the emitted patch to the new file.
            File.WriteAllText(Path.Combine(root, "review.patch"), diff);
            await GitAsync(root, "apply", "--reverse", "--check", "--", "review.patch");
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WritesPreserveExistingWindowsCasingAndExposeNewFilesWithoutStaging()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "maf-path-casing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Xbank.Marketing", "Application"));
        try
        {
            await GitAsync(root, "init", "--quiet");
            RepositoryWorkspace workspace = CreateWorkspace(root);
            string result = workspace.WriteFile("XBank.Marketing/application/NewFeature.cs", "class NewFeature { }");
            const string actual = "Xbank.Marketing/Application/NewFeature.cs";
            Assert.Contains(actual, result);
            Assert.Contains("casing was preserved", result);
            Assert.Equal(actual, workspace.ListFiles("newfeature"));
            Assert.Contains(actual + ":1:", workspace.Search("NewFeature"));
            Assert.Contains("NewFeature", workspace.ReadFile("XBANK.MARKETING/APPLICATION/NEWFEATURE.CS"));
            Assert.Contains("?? Xbank.Marketing/", workspace.GetStatus());
            string fingerprint = workspace.GetProgressFingerprint();
            Assert.Contains("Unchanged " + actual, workspace.WriteFile(actual, "class NewFeature { }"));
            Assert.Equal(fingerprint, workspace.GetProgressFingerprint());
            await GitAsync(root, "add", ".");
            Assert.Equal(actual, workspace.ListFiles("newfeature"));
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("updated")]
    [InlineData("diverged")]
    [InlineData("missing")]
    public async Task PullBaseBranchRefreshesRemoteChangesAndRejectsFailures(string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-base-pull", Guid.NewGuid().ToString("N"));
        string remote = Path.Combine(root, "remote");
        string clone = Path.Combine(root, "clone");
        Directory.CreateDirectory(remote);
        try
        {
            await GitAsync(remote, "init", "--initial-branch=main");
            File.WriteAllText(Path.Combine(remote, "task.txt"), "initial");
            await GitAsync(remote, "add", ".");
            await GitAsync(remote, "commit", "-m", "initial");
            await GitAsync(root, "clone", "--", remote, clone);
            string originalHead = await GitAsync(clone, "rev-parse", "HEAD");

            File.WriteAllText(Path.Combine(remote, "task.txt"), "latest base");
            await GitAsync(remote, "add", ".");
            await GitAsync(remote, "commit", "-m", "remote update");
            if (scenario == "diverged")
            {
                File.WriteAllText(Path.Combine(clone, "local.txt"), "local change");
                await GitAsync(clone, "add", ".");
                await GitAsync(clone, "commit", "-m", "local update");
                originalHead = await GitAsync(clone, "rev-parse", "HEAD");
            }
            else if (scenario == "missing")
            {
                await GitAsync(clone, "remote", "remove", "origin");
            }

            RepositoryWorkspace workspace = CreateWorkspace(clone);
            if (scenario == "updated")
            {
                await workspace.PullBaseBranchAsync(CancellationToken.None);
                Assert.Equal("latest base", File.ReadAllText(Path.Combine(clone, "task.txt")));
                Assert.Equal(await GitAsync(remote, "rev-parse", "HEAD"), await GitAsync(clone, "rev-parse", "HEAD"));
                Assert.Equal(string.Empty, await GitAsync(clone, "status", "--porcelain"));
            }
            else
            {
                RepositoryException exception = await Assert.ThrowsAsync<RepositoryException>(() =>
                    workspace.PullBaseBranchAsync(CancellationToken.None));
                Assert.Contains("Unable to pull base branch 'main' before starting the agent run", exception.Message);
                Assert.Equal(originalHead, await GitAsync(clone, "rev-parse", "HEAD"));
            }
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> GitAsync(string directory, params string[] arguments)
    {
        System.Diagnostics.ProcessStartInfo startInfo = RepositoryWorkspace.CreateGitStartInfo(
            directory,
            ["-c", "user.name=MAF Tests", "-c", "user.email=maf-tests@example.com", "-c", "commit.gpgSign=false", .. arguments],
            RepositoryProvider.GitHub,
            accessToken: null);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return (await output).Trim();
    }

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
