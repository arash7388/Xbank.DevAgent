using System.Diagnostics;
using System.Reflection;
using Maf.Handoff.Core;

namespace Maf.Handoff.Tests;

public sealed class RepositoryMergeRequestReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewUsesBothMrCommitsAndExcludesTargetOnlyChanges(bool sourceMoved)
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-mr-range", Guid.NewGuid().ToString("N"));
        string remote = Path.Combine(root, "remote");
        string clone = Path.Combine(root, "clone");
        Directory.CreateDirectory(remote);
        try
        {
            await Git(remote, "init", "--initial-branch=main");
            File.WriteAllText(Path.Combine(remote, "Existing.cs"), "class Existing {}\n");
            File.WriteAllText(Path.Combine(remote, "Delete.cs"), "class Delete {}\n");
            await Commit(remote, "initial");
            string mergeBase = await Git(remote, "rev-parse", "HEAD");
            await Git(remote, "checkout", "-b", "task/source");
            File.WriteAllText(Path.Combine(remote, "First.cs"), "class First {}\n");
            File.Delete(Path.Combine(remote, "Delete.cs"));
            await Commit(remote, "TASK-31 first MR commit");
            File.WriteAllText(Path.Combine(remote, "Second.cs"), "class Second {}\n");
            File.Move(Path.Combine(remote, "Existing.cs"), Path.Combine(remote, "Renamed.cs"));
            await Commit(remote, "TASK-31 second MR commit");
            string head = await Git(remote, "rev-parse", "HEAD");
            await Git(remote, "checkout", "main");
            File.WriteAllText(Path.Combine(remote, "Unrelated.cs"), "class Unrelated { bool IsBlockingReadMode; }\n");
            await Commit(remote, "unrelated target-only change");
            string target = await Git(remote, "rev-parse", "HEAD");
            if (sourceMoved)
            {
                await Git(remote, "checkout", "task/source");
                File.WriteAllText(Path.Combine(remote, "Moved.cs"), "class Moved {}\n");
                await Commit(remote, "source moved after MR read");
            }
            // file:// enforces a real shallow clone even with a local test remote.
            await Git(root, "clone", "--depth=1", "--branch", "task/source", "--", new Uri(remote + Path.DirectorySeparatorChar).AbsoluteUri, clone);
            Assert.Equal("true", await Git(clone, "rev-parse", "--is-shallow-repository"));
            RepositoryWorkspace workspace = (RepositoryWorkspace)Activator.CreateInstance(typeof(RepositoryWorkspace),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [clone, "https://gitlab.com/acme/orders.git", "task/source", null], null)!;
            GitLabReviewDiffRefs refs = new(mergeBase, head, target);
            if (sourceMoved)
            {
                var error = await Assert.ThrowsAsync<RepositoryException>(() => workspace.PrepareMergeRequestReviewAsync(refs, CancellationToken.None));
                Assert.Contains("source branch changed", error.Message);
                return;
            }

            string scope = await workspace.PrepareMergeRequestReviewAsync(refs, CancellationToken.None);
            Assert.Contains(mergeBase + ".." + head, scope);
            Assert.Contains("First.cs", scope);
            Assert.Contains("Second.cs", scope);
            Assert.DoesNotContain("Unrelated.cs", scope);
            string diff = workspace.GetDiff();
            Assert.Contains("+class First", diff);
            Assert.Contains("+class Second", diff);
            Assert.Contains("-class Delete", diff);
            Assert.Contains("Renamed.cs", diff);
            Assert.DoesNotContain("IsBlockingReadMode", diff);
            Assert.DoesNotContain("Unrelated.cs", diff);
            Assert.Empty(workspace.GetStatus());
            Assert.Empty(workspace.GetWorkingTreeDiff());

            workspace.WriteFile("First.cs", "class First { int Fixed; }\n");
            workspace.WriteFile("NewFix.cs", "class NewFix {}\n");
            string fixes = workspace.GetWorkingTreeDiff();
            Assert.Contains("Fixed", fixes);
            Assert.Contains("NewFix.cs", fixes);
            Assert.DoesNotContain("Second.cs", fixes);
            Assert.DoesNotContain("Delete.cs", fixes);
            Assert.DoesNotContain("Renamed.cs", fixes);
            Assert.Contains("Second.cs", workspace.GetDiff());
            Assert.Contains("NewFix.cs", workspace.GetDiff());
            Assert.Contains("Fixed", workspace.GetDiff());
            Assert.Contains("?? NewFix.cs", workspace.GetStatus());
            Assert.Empty(await Git(clone, "diff", "--cached"));
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task Commit(string directory, string message)
    {
        await Git(directory, "add", "--all");
        await Git(directory, "commit", "-m", message);
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        ProcessStartInfo start = RepositoryWorkspace.CreateGitStartInfo(directory,
            ["-c", "user.name=MAF Tests", "-c", "user.email=maf-tests@example.com", "-c", "commit.gpgSign=false", .. arguments],
            RepositoryProvider.GitLab, null);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return (await output).Trim();
    }
}
