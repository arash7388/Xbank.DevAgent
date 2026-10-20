using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    private readonly string? _accessToken;
    private RepositoryProvider _provider;
    private readonly Lock _progressGate = new();
    private string? _progressFingerprint;

    private RepositoryWorkspace(string rootPath, string repositoryUrl, string baseBranch, string? accessToken)
    {
        RootPath = rootPath;
        RepositoryUrl = repositoryUrl;
        BaseBranch = baseBranch;
        _accessToken = accessToken;
        _provider = Uri.TryCreate(repositoryUrl, UriKind.Absolute, out Uri? uri) &&
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                ? RepositoryProvider.GitHub
                : RepositoryProvider.GitLab;
    }

    public string RootPath { get; }

    public string RepositoryUrl { get; }

    public string BaseBranch { get; private set; }

    /// <summary>Returns whether the cloned repository contains at least one non-ignored C# source file.</summary>
    public bool ContainsCSharpSource() => EnumerateSourceFiles()
        .Any(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

    public static async Task<RepositoryWorkspace> CloneAsync(
        string repositoryUrl,
        string baseBranch,
        string? githubToken,
        string workspaceRoot,
        CancellationToken cancellationToken,
        RepositoryProvider provider = RepositoryProvider.GitHub,
        string? gitLabHost = null)
    {
        ValidateRepositoryUrl(repositoryUrl, provider, gitLabHost);
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
        RepositoryWorkspace workspace = new(clonePath, repositoryUrl, baseBranch, githubToken)
        {
            _provider = provider,
        };
        workspace.BaseBranch = ResolveBaseBranch(baseBranch, await ListBranchesAsync(
            repositoryUrl, githubToken, cancellationToken, provider, gitLabHost));
        GitResult result = await workspace.RunGitAsync(
            workingDirectory: root,
            ["clone", "--depth", "1", "--branch", workspace.BaseBranch, "--", repositoryUrl, clonePath],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new RepositoryException($"Git clone failed: {SanitizeGitError(result.StandardError)}");
        }

        await workspace.PullBaseBranchAsync(cancellationToken);
        return workspace;
    }

    internal async Task PullBaseBranchAsync(CancellationToken cancellationToken)
    {
        GitResult result = await RunGitAsync(
            RootPath,
            ["pull", "--ff-only", "--no-rebase", "origin", BaseBranch],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new RepositoryException(
                $"Unable to pull base branch '{BaseBranch}' before starting the agent run: {SanitizeGitError(result.StandardError)}");
        }
    }

    public static async Task<string[]> ListBranchesAsync(
        string repositoryUrl,
        string? accessToken,
        CancellationToken cancellationToken,
        RepositoryProvider provider = RepositoryProvider.GitHub,
        string? gitLabHost = null)
    {
        ValidateRepositoryUrl(repositoryUrl, provider, gitLabHost);
        RepositoryWorkspace workspace = new(Path.GetTempPath(), repositoryUrl, string.Empty, accessToken)
        {
            _provider = provider,
        };
        GitResult branches = await workspace.RunGitAsync(
            workspace.RootPath, ["ls-remote", "--heads", "--", repositoryUrl], cancellationToken);
        if (branches.ExitCode != 0)
        {
            throw new RepositoryException($"Unable to list repository branches: {SanitizeGitError(branches.StandardError)}");
        }

        return ParseRemoteBranches(branches.StandardOutput);
    }

    internal static string[] ParseRemoteBranches(string output)
    {
        const string headPrefix = "refs/heads/";
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t', 2))
            .Where(parts => parts.Length == 2 && parts[1].StartsWith(headPrefix, StringComparison.Ordinal))
            .Select(parts => parts[1][headPrefix.Length..])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    internal static string ResolveBaseBranch(string requestedBranch, IEnumerable<string> remoteBranches)
    {
        string[] matches = remoteBranches
            .Where(branch => branch.Equals(requestedBranch, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (matches.Contains(requestedBranch, StringComparer.Ordinal))
        {
            return requestedBranch;
        }

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new RepositoryException($"Base branch '{requestedBranch}' was not found in the repository."),
            _ => throw new RepositoryException(
                $"Base branch '{requestedBranch}' matches multiple branches: {string.Join(", ", matches)}. Enter the exact branch name."),
        };
    }

    [Description("Lists tracked and untracked, non-ignored source files in the cloned repository, including newly written files. The optional pattern is a case-insensitive substring.")]
    public string ListFiles(string? pattern = null)
    {
        IEnumerable<string> files = EnumerateSourceFiles(pattern);
        return Limit(string.Join('\n', files.Take(1_500)));
    }

    private IEnumerable<string> EnumerateSourceFiles(string? pattern = null)
    {
        GitResult result = RunGitAsync(RootPath,
            ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], CancellationToken.None,
            truncateOutput: false).GetAwaiter().GetResult();
        if (result.ExitCode != 0)
            throw new RepositoryException($"Unable to list repository files: {SanitizeGitError(result.StandardError)}");
        IEnumerable<string> files = result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Where(IsAllowedSourceFile);
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            files = files.Where(path => path.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        }

        return files.Where(path => File.Exists(ResolveSourcePath(path, mustExist: false)));
    }

    [Description("Reads one text source file. Paths must be relative to the repository and cannot access .git or secrets.")]
    public string ReadFile([Description("Repository-relative path returned by list_repository_files.")] string path)
        => ReadFileCore(path, compact: false);

    [Description("Reads one text source file in a compact, token-efficient form for repository planning.")]
    public string ReadFileCompact(
        [Description("Repository-relative path returned by list_repository_files.")] string path)
        => ReadFileCore(path, compact: true);

    private string ReadFileCore(string path, bool compact)
    {
        string fullPath = ResolveSourcePath(path, mustExist: false);
        if (!File.Exists(fullPath))
        {
            return MissingSourceFile(path);
        }

        FileInfo file = new(fullPath);
        if (file.Length > MaxFileBytes)
        {
            return $"File is too large to read safely ({file.Length} bytes; limit {MaxFileBytes}).";
        }

        string content = File.ReadAllText(fullPath);
        return compact ? CompactFileContent(path, content) : content;
    }

    private static string CompactFileContent(string path, string content)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(content);
                return JsonSerializer.Serialize(document.RootElement);
            }
            catch (JsonException)
            {
                // Preserve malformed JSON for diagnosis and apply only safe whitespace normalization.
            }
        }

        string normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        StringBuilder compact = new(normalized.Length);
        bool previousLineWasBlank = false;
        foreach (string line in lines)
        {
            string trimmed = line.TrimEnd();
            bool isBlank = trimmed.Length == 0;
            if (isBlank && previousLineWasBlank)
            {
                continue;
            }

            if (compact.Length > 0)
            {
                compact.Append('\n');
            }

            compact.Append(trimmed);
            previousLineWasBlank = isBlank;
        }

        return compact.ToString().TrimEnd('\n');
    }

    [Description("Searches tracked and untracked, non-ignored text source files, including newly written files, and returns matching file names, line numbers, and lines.")]
    public string Search(
        [Description("Literal case-insensitive text to find.")] string query,
        [Description("Optional file path substring filter.")] string? pathFilter = null)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200)
        {
            return "Search query must contain between 1 and 200 characters.";
        }

        List<string> matches = [];
        foreach (string path in EnumerateSourceFiles(pathFilter))
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
        if (OperatingSystem.IsWindows()) fullPath = PreserveExistingPathCasing(fullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        lock (_progressGate)
        {
            if (File.Exists(fullPath) && new FileInfo(fullPath).Length == Encoding.UTF8.GetByteCount(content) &&
                File.ReadAllBytes(fullPath).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(content)))
                return $"Unchanged {RepositoryRelativePath(fullPath)}; identical content already exists. Read it directly or inspect get_repository_diff instead of repeating this write.";
            File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _progressFingerprint = null;
        }
        string actualPath = RepositoryRelativePath(fullPath);
        string casingNote = path.Replace('\\', '/') == actualPath ? "" : " Existing path casing was preserved.";
        return $"Wrote {actualPath} ({Encoding.UTF8.GetByteCount(content)} bytes).{casingNote}";
    }

    [Description("Replaces one unique text occurrence in an existing source file. Matching tolerates line-ending differences only; replacement preserves local line endings and all unrelated content.")]
    public string ReplaceText(
        [Description("Repository-relative source file path.")] string path,
        [Description("Existing text to replace, including exact indentation and whitespace. LF, CRLF and CR line endings are equivalent. The edit is rejected unless it occurs exactly once.")] string oldText,
        [Description("Replacement text.")] string newText)
    {
        if (string.IsNullOrEmpty(oldText))
        {
            return "Existing text must not be empty.";
        }

        string fullPath = ResolveSourcePath(path, mustExist: false);
        if (!File.Exists(fullPath))
        {
            return MissingSourceFile(path);
        }

        byte[] originalBytes = File.ReadAllBytes(fullPath);
        if (originalBytes.Length > MaxFileBytes)
        {
            return $"File is too large to edit safely ({originalBytes.Length} bytes; limit {MaxFileBytes}).";
        }

        bool hasUtf8Bom = originalBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        string content = File.ReadAllText(fullPath);
        // Models may use LF even when the source uses CRLF. Map normalized offsets back
        // to the original text so matching never rewrites unrelated lines or whitespace.
        (string matchContent, int[] offsets) = NormalizeEditContent(content);
        string matchText = NormalizeEditLineEndings(oldText, "\n");
        int first = matchContent.IndexOf(matchText, StringComparison.Ordinal);
        if (first < 0)
        {
            return "Exact existing text was not found; no change was made.";
        }

        if (matchContent.IndexOf(matchText, first + matchText.Length, StringComparison.Ordinal) >= 0)
        {
            return "Exact existing text occurs more than once; narrow the match before editing.";
        }

        int start = offsets[first];
        int end = offsets[first + matchText.Length];
        string lineEnding = FindLineEnding(content[start..end]) ?? FindLineEnding(content) ?? "\n";
        string updated = string.Concat(content.AsSpan(0, start), NormalizeEditLineEndings(newText, lineEnding), content.AsSpan(end));
        if (Encoding.UTF8.GetByteCount(updated) > MaxFileBytes)
        {
            return $"Updated content exceeds the {MaxFileBytes}-byte write limit.";
        }

        lock (_progressGate)
        {
            File.WriteAllText(fullPath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasUtf8Bom));
            _progressFingerprint = null;
        }
        return $"Replaced one exact occurrence in {path}.";
    }

    private static (string Text, int[] Offsets) NormalizeEditContent(string content)
    {
        StringBuilder normalized = new(content.Length);
        int[] offsets = new int[content.Length + 1];
        for (int index = 0; index < content.Length; index++)
        {
            offsets[normalized.Length] = index;
            char character = content[index];
            normalized.Append(character == '\r' ? '\n' : character);
            if (character == '\r' && index + 1 < content.Length && content[index + 1] == '\n') index++;
        }
        offsets[normalized.Length] = content.Length;
        return (normalized.ToString(), offsets);
    }

    private static string NormalizeEditLineEndings(string text, string lineEnding) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Replace("\n", lineEnding, StringComparison.Ordinal);

    private static string? FindLineEnding(string content)
    {
        int index = content.IndexOfAny(['\r', '\n']);
        if (index < 0) return null;
        return content[index] == '\r' && index + 1 < content.Length && content[index + 1] == '\n'
            ? "\r\n" : content[index].ToString();
    }

    [Description("Returns the current Git status for the isolated clone.")]
    public string GetStatus() => Limit(RunGit(["status", "--short"]).StandardOutput);

    [Description("Returns the exact uncommitted Git diff, including newly created source files.")]
    public string GetDiff()
    {
        GitResult tracked = RunGit(["diff", "--no-ext-diff", "--"]);
        GitResult untracked = RunGitAsync(RootPath,
            ["ls-files", "--others", "--exclude-standard", "-z"], CancellationToken.None,
            truncateOutput: false).GetAwaiter().GetResult();
        if (untracked.ExitCode != 0)
            throw new RepositoryException($"Unable to inspect new files: {SanitizeGitError(untracked.StandardError)}");
        StringBuilder diff = new(tracked.StandardOutput);
        foreach (string path in untracked.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsAllowedSourceFile(path))
            {
                continue;
            }

            string fullPath = ResolveSourcePath(path, mustExist: true);
            if (new FileInfo(fullPath).Length <= MaxFileBytes)
            {
                diff.Append($"diff --git a/{path} b/{path}\n");
                diff.Append("new file mode 100644\n");
                diff.Append("--- /dev/null\n");
                diff.Append($"+++ b/{path}\n");
                string content = File.ReadAllText(fullPath);
                string[] lines = content.Length == 0 ? [] : content.Split('\n');
                int lineCount = lines.Length - (content.EndsWith('\n') ? 1 : 0);
                if (lineCount > 0)
                    diff.Append($"@@ -0,0 +1,{lineCount} @@\n");
                for (int index = 0; index < lineCount; index++)
                {
                    diff.Append('+').Append(lines[index]).Append('\n');
                }
                if (lineCount > 0 && !content.EndsWith('\n'))
                    diff.Append("\\ No newline at end of file\n");
            }
        }

        return Limit(diff.ToString());
    }

    internal string GetProgressFingerprint()
    {
        // Agents mutate this isolated clone only through WriteFile/ReplaceText, which invalidate
        // the cached content hash. Ordinary reads and handoffs need no additional Git processes.
        lock (_progressGate) return _progressFingerprint ??= ComputeProgressFingerprint();
    }

    private string ComputeProgressFingerprint()
    {
        // One Git query plus full hashes of changed files; display truncation must not hide progress.
        GitResult status = RunGitAsync(RootPath,
            ["status", "--porcelain=v1", "-z", "--untracked-files=all"], CancellationToken.None,
            truncateOutput: false).GetAwaiter().GetResult();
        if (status.ExitCode != 0)
            throw new RepositoryException($"Unable to inspect workflow progress: {SanitizeGitError(status.StandardError)}");
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(status.StandardOutput));
        string[] entries = status.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < entries.Length; index++)
        {
            string entry = entries[index];
            if (entry.Length < 4) continue;
            string path = entry[3..];
            // With -z, a rename/copy has a second NUL-delimited original path.
            if (entry[..2].Contains('R') || entry[..2].Contains('C')) index++;
            if (!IsAllowedSourceFile(path)) continue;
            string fullPath = ResolveSourcePath(path, mustExist: false);
            if (!File.Exists(fullPath)) continue; // Deleted paths are already represented in status.
            using FileStream file = File.OpenRead(fullPath);
            hash.AppendData(SHA256.HashData(file));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
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
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            throw new RepositoryException($"A {_provider.DisplayName()} token is required to push the approved branch.");
        }

        await EnsureGitSuccessAsync(["push", "origin", $"HEAD:refs/heads/{branch}"], cancellationToken);
    }

    internal static void ValidateRepositoryUrl(
        string repositoryUrl,
        RepositoryProvider provider,
        string? gitLabHost)
    {
        string requiredHost = provider == RepositoryProvider.GitHub
            ? "github.com"
            : string.IsNullOrWhiteSpace(gitLabHost) ? "gitlab.com" : gitLabHost.Trim();
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, requiredHost, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new RepositoryException(
                $"Repository URL must be an HTTPS {requiredHost} URL for {provider.DisplayName()}.");
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        bool invalidSegmentCount = provider == RepositoryProvider.GitHub
            ? segments.Length != 2
            : segments.Length < 2;
        if (invalidSegmentCount || segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new RepositoryException(provider == RepositoryProvider.GitHub
                ? "Repository URL must identify one owner and repository."
                : "Repository URL must identify a GitLab namespace and project.");
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

    private string RepositoryRelativePath(string fullPath) => Path.GetRelativePath(RootPath, fullPath).Replace('\\', '/');

    private string PreserveExistingPathCasing(string fullPath)
    {
        string current = RootPath;
        foreach (string segment in Path.GetRelativePath(RootPath, fullPath).Split(Path.DirectorySeparatorChar))
        {
            string[] matches = Directory.Exists(current)
                ? Directory.EnumerateFileSystemEntries(current).Where(entry =>
                    Path.GetFileName(entry).Equals(segment, StringComparison.OrdinalIgnoreCase)).ToArray()
                : [];
            string? existing = matches.FirstOrDefault(entry => Path.GetFileName(entry).Equals(segment, StringComparison.Ordinal));
            if (existing is null && matches.Length > 1)
                throw new RepositoryException($"Path casing is ambiguous: {segment}. Use the exact repository path.");
            existing ??= matches.FirstOrDefault();
            current = existing ?? Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new RepositoryException("Symbolic links are not available to repository tools.");
        }
        return current;
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

    private static string MissingSourceFile(string path) =>
        $"Source file does not exist: {path}. Use list_repository_files or search_repository to find the exact source path; no change was made.";

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
        CancellationToken cancellationToken,
        bool truncateOutput = true)
    {
        ProcessStartInfo startInfo = CreateGitStartInfo(workingDirectory, arguments, _provider, _accessToken);
        using Process process = Process.Start(startInfo) ?? throw new RepositoryException("Unable to start Git.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string output = await outputTask;
        return new(process.ExitCode, truncateOutput ? Limit(output) : output, Limit(await errorTask));
    }

    internal static ProcessStartInfo CreateGitStartInfo(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        RepositoryProvider provider,
        string? accessToken)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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

        // Background Git operations must never open a credential-manager login dialog.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            // Reset inherited helpers and authorization headers so only the supplied token is used.
            startInfo.Environment[$"GIT_CONFIG_KEY_{configIndex}"] = "credential.helper";
            startInfo.Environment[$"GIT_CONFIG_VALUE_{configIndex}"] = string.Empty;
            configIndex++;
            startInfo.Environment[$"GIT_CONFIG_KEY_{configIndex}"] = "http.extraHeader";
            startInfo.Environment[$"GIT_CONFIG_VALUE_{configIndex}"] = string.Empty;
            configIndex++;
            string userName = provider == RepositoryProvider.GitHub ? "x-access-token" : "oauth2";
            string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{accessToken.Trim()}"));
            startInfo.Environment[$"GIT_CONFIG_KEY_{configIndex}"] = "http.extraHeader";
            startInfo.Environment[$"GIT_CONFIG_VALUE_{configIndex}"] = $"Authorization: Basic {credentials}";
            configIndex++;
        }

        startInfo.Environment["GIT_CONFIG_COUNT"] = configIndex.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";

        return startInfo;
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
