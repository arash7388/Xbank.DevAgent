using System.Diagnostics;
using System.Reflection;
using System.Text;
using Maf.Handoff.Core;

namespace Maf.Handoff.Tests;

public sealed class RepositoryScenarioTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maf-repository-scenarios-" + Guid.NewGuid().ToString("N"));
    private readonly RepositoryWorkspace _workspace;

    public RepositoryScenarioTests()
    {
        Directory.CreateDirectory(_root);
        _workspace = (RepositoryWorkspace)Activator.CreateInstance(typeof(RepositoryWorkspace),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [_root, "https://github.com/example/test", "main", null], null)!;
        Git("init", "--initial-branch=main");
        // Make quoting deterministic regardless of the host's Git configuration.
        Git("config", "core.quotePath", "true");
        File.WriteAllText(Path.Combine(_root, "README.md"), "Test repository\n");
        Git("add", "README.md");
        Git("commit", "-m", "initial");
    }

    [Theory]
    [InlineData("list")]
    [InlineData("search")]
    [InlineData("csharp-detection")]
    [InlineData("diff")]
    public void NewlyWrittenSourceIsVisibleToAllInspectionTools(string operation)
    {
        _workspace.WriteFile("src/NewFeature.cs", "public class NewFeature { }\n");
        Assert.Contains("NewFeature", _workspace.ReadFile("src/NewFeature.cs"));
        switch (operation)
        {
            case "list": Assert.Contains("src/NewFeature.cs", _workspace.ListFiles()); break;
            case "search": Assert.Contains("src/NewFeature.cs:1:", _workspace.Search("NewFeature")); break;
            case "csharp-detection": Assert.True(_workspace.ContainsCSharpSource()); break;
            case "diff": Assert.Contains("+public class NewFeature", _workspace.GetDiff()); break;
        }
        Assert.Contains("?? src/", _workspace.GetStatus());
    }

    [Theory]
    [InlineData("list")]
    [InlineData("search")]
    [InlineData("diff")]
    public void UnicodeSourcePathsRemainDiscoverable(string operation)
    {
        const string path = "src/پرداخت.cs";
        _workspace.WriteFile(path, "public class Payment { }\n");
        if (operation != "diff") Git("add", "--", path);
        switch (operation)
        {
            case "list": Assert.Contains(path, _workspace.ListFiles()); break;
            case "search": Assert.Contains(path + ":1:", _workspace.Search("Payment")); break;
            case "diff": Assert.Contains("+public class Payment", _workspace.GetDiff()); break;
        }
    }

    [Fact]
    public void SearchFindsSourceBeyondTheListPresentationLimit()
    {
        for (int index = 0; index < 1501; index++)
            File.WriteAllText(Path.Combine(_root, $"a{index:D4}.txt"), "ordinary content");
        File.WriteAllText(Path.Combine(_root, "z-target.cs"), "public class LastSource { }");
        Git("add", ".");
        Assert.Contains("z-target.cs:1:", _workspace.Search("LastSource"));
    }

    [Fact]
    public void DeletedTrackedFileDoesNotPreventSearchingRemainingFiles()
    {
        _workspace.WriteFile("a-deleted.cs", "old source");
        _workspace.WriteFile("z-remaining.cs", "public class Remaining { }");
        Git("add", ".");
        File.Delete(Path.Combine(_root, "a-deleted.cs"));
        Assert.Contains("z-remaining.cs:1:", _workspace.Search("Remaining"));
    }

    [Fact]
    public void IgnoredAndUnsupportedFilesAreExcludedFromSearch()
    {
        _workspace.WriteFile(".gitignore", "ignored/\n");
        _workspace.WriteFile("src/Allowed.cs", "Needle");
        _workspace.WriteFile("ignored/Generated.cs", "Needle");
        File.WriteAllText(Path.Combine(_root, "secret.env"), "Needle");
        Git("add", ".");
        string results = _workspace.Search("Needle");
        Assert.Contains("src/Allowed.cs", results);
        Assert.DoesNotContain("Generated", results);
        Assert.DoesNotContain("secret.env", results);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData(".git/config")]
    [InlineData(".git/metadata.json")]
    [InlineData("secrets.env")]
    [InlineData("binary.dll")]
    public void ReadAndWriteRejectPathsOutsideTheSourceBoundary(string path)
    {
        Assert.Throws<RepositoryException>(() => _workspace.ReadFile(path));
        Assert.Throws<RepositoryException>(() => _workspace.WriteFile(path, "must not write"));
        Assert.Throws<RepositoryException>(() => _workspace.ReplaceText(path, "old", "new"));
        Assert.Equal("", _workspace.GetStatus());
    }

    [Theory]
    [InlineData(199999, true)]
    [InlineData(200000, true)]
    [InlineData(200001, false)]
    public void WriteLimitIsEnforcedAtTheByteBoundary(int bytes, bool accepted)
    {
        _workspace.WriteFile("limit.txt", "original");
        string content = new('x', bytes);
        string result = _workspace.WriteFile("limit.txt", content);
        Assert.Equal(accepted ? content : "original", File.ReadAllText(Path.Combine(_root, "limit.txt")));
        Assert.Contains(accepted ? "Wrote" : "limit", result);
    }

    [Fact]
    public void WriteLimitCountsUtf8BytesRatherThanCharacters()
    {
        string content = new('ش', 100001);
        Assert.True(Encoding.UTF8.GetByteCount(content) > 200000);
        Assert.Contains("limit", _workspace.WriteFile("unicode.txt", content));
        Assert.False(File.Exists(Path.Combine(_root, "unicode.txt")));
    }

    [Fact]
    public void FailedReplacementLeavesFileAndProgressFingerprintUnchanged()
    {
        _workspace.WriteFile("source.cs", "same\nsame\n");
        string fingerprint = _workspace.GetProgressFingerprint();
        _workspace.ReplaceText("source.cs", "same", "changed");
        Assert.Equal("same\nsame\n", _workspace.ReadFile("source.cs"));
        Assert.Equal(fingerprint, _workspace.GetProgressFingerprint());
    }

    private void Git(params string[] arguments)
    {
        ProcessStartInfo start = RepositoryWorkspace.CreateGitStartInfo(_root,
            ["-c", "user.name=MAF Tests", "-c", "user.email=maf-tests@example.test", "-c", "commit.gpgSign=false", .. arguments],
            RepositoryProvider.GitHub, null);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Temporary repository setup exceeded 15 seconds.");
        }
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
        _ = output.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
