using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Maf.Handoff.Core;

public sealed partial class RepositoryWorkspace
{
    private const int MaxFileBytes = 200_000;
    private const int MaxToolOutputCharacters = 300_000;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".json", ".jsonc",
        ".js", ".jsx", ".ts", ".tsx", ".css", ".scss", ".html", ".htm", ".md",
        ".py", ".toml", ".yaml", ".yml", ".xml", ".sql", ".sh", ".ps1", ".txt",
        ".go", ".rs", ".java", ".kt", ".cpp", ".c", ".h", ".hpp", ".gitignore",
    };
    private static readonly HashSet<string> AllowedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Makefile", "LICENSE", "global.json", ".editorconfig", ".gitignore",
    };

    private readonly string? _githubToken;

    private RepositoryWorkspace(string rootPath, string repositoryUrl, string baseBranch, string? githubToken)
    {
        RootPath = rootPath;
        RepositoryUrl = repositoryUrl;
        BaseBranch = baseBranch;
        _githubToken = githubToken;
    }

    public string RootPath { get; }

    public string RepositoryUrl { get; }

    public string BaseBranch { get; }

    /// <summary>Returns whether the cloned repository tracks at least one C# source file.</summary>
    public bool ContainsCSharpSource() => ListFiles().Split(
        '\n',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

    public static async Task<RepositoryWorkspace> CloneAsync(
        string repositoryUrl,
        string baseBranch,
        string? githubToken,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        ValidateRepositoryUrl(repositoryUrl);
        ValidateBranchName(baseBranch);

        string root = Path.GetFullPath(workspaceRoot);
        Directory.CreateDirectory(root);
        string clonePath = Path.Combine(root, Guid.NewGuid().ToString("N"));
        if (!Path.GetFullPath(clonePath).StartsWith(root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The clone path escaped the configured workspace.");
        }

        Directory.CreateDirectory(clonePath);
        RepositoryWorkspace workspace = new(clonePath, repositoryUrl, baseBranch, githubToken);
        GitResult result = await workspace.RunGitAsync(
            workingDirectory: root,
            ["clone", "--depth", "1", "--branch", baseBranch, "--", repositoryUrl, clonePath],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new RepositoryException($"Git clone failed: {SanitizeGitError(result.StandardError)}");
        }

        return workspace;
    }

    [Description("Lists tracked source files in the cloned repository. The optional pattern is a case-insensitive substring.")]
    public string ListFiles(string? pattern = null)
    {
        GitResult result = RunGit(["ls-files"]);
        IEnumerable<string> files = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim())
            .Where(IsAllowedSourceFile);
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            files = files.Where(path => path.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        }

        return Limit(string.Join('\n', files.Take(1_500)));
    }

    [Description("Reads one text source file. Paths must be relative to the repository and cannot access .git or secrets.")]
    public string ReadFile([Description("Repository-relative path returned by list_repository_files.")] string path)
    {
        string fullPath = ResolveSourcePath(path, mustExist: true);
        FileInfo file = new(fullPath);
        if (file.Length > MaxFileBytes)
        {
            return $"File is too large to read safely ({file.Length} bytes; limit {MaxFileBytes}).";
        }

        return File.ReadAllText(fullPath);
    }

    [Description("Searches tracked text source files and returns matching file names, line numbers, and lines.")]
    public string Search(
        [Description("Literal case-insensitive text to find.")] string query,
        [Description("Optional file path substring filter.")] string? pathFilter = null)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200)
        {
            return "Search query must contain between 1 and 200 characters.";
        }

        List<string> matches = [];
        foreach (string path in ListFiles(pathFilter).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string fullPath = ResolveSourcePath(path, mustExist: true);
            if (new FileInfo(fullPath).Length > MaxFileBytes)
            {
                continue;
            }

            int lineNumber = 0;
            foreach (string line in File.ReadLines(fullPath))
            {
                lineNumber++;
                if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add($"{path}:{lineNumber}: {line.Trim()}");
                    if (matches.Count >= 250)
                    {
                        return string.Join('\n', matches);
                    }
                }
            }
        }

        return matches.Count == 0 ? "No matches." : string.Join('\n', matches);
    }

    [Description("Writes a complete text source file inside the isolated clone. This never modifies the original repository directly.")]
    public string WriteFile(
        [Description("Repository-relative source file path.")] string path,
        [Description("Complete replacement contents for the file.")] string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxFileBytes)
        {
            return $"Content exceeds the {MaxFileBytes}-byte write limit.";
        }

        string fullPath = ResolveSourcePath(path, mustExist: false);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return $"Wrote {path} ({Encoding.UTF8.GetByteCount(content)} bytes).";
    }

    [Description("Replaces one exact text occurrence in an existing source file while preserving all unrelated content.")]
    public string ReplaceText(
        [Description("Repository-relative source file path.")] string path,
        [Description("Exact existing text to replace. The edit is rejected unless it occurs exactly once.")] string oldText,
        [Description("Replacement text.")] string newText)
    {
        if (string.IsNullOrEmpty(oldText))
        {
            return "Existing text must not be empty.";
        }

        string fullPath = ResolveSourcePath(path, mustExist: true);
        byte[] originalBytes = File.ReadAllBytes(fullPath);
        if (originalBytes.Length > MaxFileBytes)
        {
            return $"File is too large to edit safely ({originalBytes.Length} bytes; limit {MaxFileBytes}).";
        }

        bool hasUtf8Bom = originalBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        string content = File.ReadAllText(fullPath);
        int first = content.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0)
        {
            return "Exact existing text was not found; no change was made.";
        }

        if (content.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
        {
            return "Exact existing text occurs more than once; narrow the match before editing.";
        }

        string updated = string.Concat(content.AsSpan(0, first), newText, content.AsSpan(first + oldText.Length));
        if (Encoding.UTF8.GetByteCount(updated) > MaxFileBytes)
        {
            return $"Updated content exceeds the {MaxFileBytes}-byte write limit.";
        }

        File.WriteAllText(fullPath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasUtf8Bom));
        return $"Replaced one exact occurrence in {path}.";
    }

    [Description("Returns the current Git status for the isolated clone.")]
    public string GetStatus() => Limit(RunGit(["status", "--short"]).StandardOutput);

    [Description("Returns the exact uncommitted Git diff, including newly created source files.")]
    public string GetDiff()
    {
        GitResult tracked = RunGit(["diff", "--no-ext-diff", "--"]);
        GitResult untracked = RunGit(["ls-files", "--others", "--exclude-standard"]);
        StringBuilder diff = new(tracked.StandardOutput);
        foreach (string path in untracked.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsAllowedSourceFile(path))
            {
                continue;
            }

            string fullPath = ResolveSourcePath(path, mustExist: true);
            if (new FileInfo(fullPath).Length <= MaxFileBytes)
            {
                diff.AppendLine($"diff --git a/{path} b/{path}");
                diff.AppendLine("new file mode 100644");
                diff.AppendLine("--- /dev/null");
                diff.AppendLine($"+++ b/{path}");
                foreach (string line in File.ReadLines(fullPath))
                {
                    diff.Append('+').AppendLine(line);
                }
            }
        }

        return Limit(diff.ToString());
    }

    public async Task<ApprovalResult> CommitAsync(
        string branch,
        string commitMessage,
        bool push,
        CancellationToken cancellationToken)
    {
        ValidateBranchName(branch);
        if (string.IsNullOrWhiteSpace(commitMessage) || commitMessage.Length > 200)
        {
            throw new RepositoryException("Commit message must contain between 1 and 200 characters.");
        }

        await EnsureGitSuccessAsync(["switch", "-c", branch], cancellationToken);
        await EnsureGitSuccessAsync(["add", "--all"], cancellationToken);
        await EnsureGitSuccessAsync(
            ["-c", "user.name=MAF Repository Agent", "-c", "user.email=maf-agent@local.invalid",
                "commit", "-m", commitMessage],
            cancellationToken);
        string commit = (await EnsureGitSuccessAsync(["rev-parse", "HEAD"], cancellationToken)).Trim();

        if (push)
        {
            await PushAsync(branch, cancellationToken);
        }

        return new(branch, commit, push);
    }

    public async Task PushAsync(string branch, CancellationToken cancellationToken)
    {
        ValidateBranchName(branch);
        if (string.IsNullOrWhiteSpace(_githubToken))
        {
            throw new RepositoryException("A GitHub token is required to push the approved branch.");
        }

        await EnsureGitSuccessAsync(["push", "origin", $"HEAD:refs/heads/{branch}"], cancellationToken);
    }

    private static void ValidateRepositoryUrl(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new RepositoryException("Repository URL must be an HTTPS github.com URL.");
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2 || segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new RepositoryException("Repository URL must identify one owner and repository.");
        }
    }

    public static void ValidateBranchName(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.Length > 150 ||
            !BranchNameRegex().IsMatch(branch) || branch.Contains("..", StringComparison.Ordinal) ||
            branch.Contains("@{", StringComparison.Ordinal) || branch.EndsWith('.') || branch.EndsWith('/'))
        {
            throw new RepositoryException("Branch name contains unsupported characters or sequences.");
        }
    }

    private string ResolveSourcePath(string relativePath, bool mustExist)
    {
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || normalized.Split(Path.DirectorySeparatorChar).Contains(".git"))
        {
            throw new RepositoryException("Only repository-relative source paths are allowed.");
        }

        string fullPath = Path.GetFullPath(Path.Combine(RootPath, normalized));
        if (!fullPath.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !IsAllowedSourceFile(relativePath))
        {
            throw new RepositoryException("The requested path is outside the allowed source workspace.");
        }

        EnsureNoReparsePoints(fullPath);
        if (File.Exists(fullPath) && new FileInfo(fullPath).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new RepositoryException("Symbolic links are not available to repository tools.");
        }

        if (mustExist && !File.Exists(fullPath))
        {
            throw new RepositoryException($"Source file does not exist: {relativePath}");
        }

        return fullPath;
    }

    private void EnsureNoReparsePoints(string path)
    {
        string? current = Path.GetDirectoryName(path);
        while (current is not null && current.StartsWith(RootPath, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) &&
                new DirectoryInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new RepositoryException("Symbolic links are not available to repository tools.");
            }

            if (string.Equals(current, RootPath, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static bool IsAllowedSourceFile(string path)
    {
        string fileName = Path.GetFileName(path);
        string extension = Path.GetExtension(fileName);
        return AllowedExtensions.Contains(extension) || AllowedNames.Contains(fileName);
    }

    private GitResult RunGit(IReadOnlyList<string> arguments) =>
        RunGitAsync(RootPath, arguments, CancellationToken.None).GetAwaiter().GetResult();

    private async Task<string> EnsureGitSuccessAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        GitResult result = await RunGitAsync(RootPath, arguments, token);
        if (result.ExitCode != 0)
        {
            throw new RepositoryException($"Git operation failed: {SanitizeGitError(result.StandardError)}");
        }

        return result.StandardOutput;
    }

    private async Task<GitResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        string emptyHooksPath = Path.Combine(Path.GetTempPath(), "maf-empty-git-hooks");
        Directory.CreateDirectory(emptyHooksPath);
        int configIndex = 0;
        startInfo.Environment[$"GIT_CONFIG_KEY_{configIndex}"] = "core.hooksPath";
        startInfo.Environment[$"GIT_CONFIG_VALUE_{configIndex}"] = emptyHooksPath;
        configIndex++;

        if (!string.IsNullOrWhiteSpace(_githubToken))
        {
            string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{_githubToken}"));
            startInfo.Environment[$"GIT_CONFIG_KEY_{configIndex}"] = "http.extraHeader";
            startInfo.Environment[$"GIT_CONFIG_VALUE_{configIndex}"] = $"Authorization: Basic {credentials}";
            configIndex++;
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        }

        startInfo.Environment["GIT_CONFIG_COUNT"] = configIndex.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";

        using Process process = Process.Start(startInfo) ?? throw new RepositoryException("Unable to start Git.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new(process.ExitCode, Limit(await outputTask), Limit(await errorTask));
    }

    private static string SanitizeGitError(string error) =>
        error.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<user>",
            StringComparison.OrdinalIgnoreCase).Trim();

    private static string Limit(string value) => value.Length <= MaxToolOutputCharacters
        ? value
        : value[..MaxToolOutputCharacters] + "\n[output truncated]";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex BranchNameRegex();

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);
}

public sealed record ApprovalResult(string Branch, string Commit, bool Pushed);

public sealed class RepositoryException(string message) : Exception(message);
