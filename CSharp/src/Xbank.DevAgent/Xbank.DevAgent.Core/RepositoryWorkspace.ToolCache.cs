using System.Security.Cryptography;
using System.Text;

namespace Xbank.DevAgent.Core;

public sealed partial class RepositoryWorkspace
{
    private long _readCacheRevision;

    internal string? GetReadCacheVersion(string? filePath = null)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(Interlocked.Read(ref _readCacheRevision).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(_reviewDiffRefs?.BaseSha ?? "");
        Add(_reviewDiffRefs?.HeadSha ?? "");
        if (filePath is not null)
        {
            string path = ResolveSourcePath(filePath, mustExist: false);
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes) return null;
            using FileStream stream = File.OpenRead(path);
            hash.AppendData(SHA256.HashData(stream));
        }
        else
        {
            // Metadata is cheaper than another full text scan and also catches edits,
            // additions, deletions and renames made outside the agent write tools.
            GitResult listing = RunGitAsync(RootPath,
                ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], CancellationToken.None,
                truncateOutput: false).GetAwaiter().GetResult();
            if (listing.ExitCode != 0) return null;
            foreach (string relative in listing.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                string path = Path.GetFullPath(Path.Combine(RootPath, relative));
                if (!path.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
                FileInfo file = new(path);
                Add(relative);
                Add(file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}:{file.CreationTimeUtc.Ticks}:{(int)file.Attributes}" : "missing");
            }
            string git = Path.Combine(RootPath, ".git");
            if (!Directory.Exists(git)) return null; // Unknown worktree layout: execute without caching.
            foreach (string name in new[] { "HEAD", "index", "packed-refs", "config", "info/exclude" })
            {
                string path = Path.Combine(git, name);
                Add(name);
                if (File.Exists(path)) hash.AppendData(SHA256.HashData(File.ReadAllBytes(path)));
            }
            string headPath = Path.Combine(git, "HEAD");
            string head = File.Exists(headPath) ? File.ReadAllText(headPath).Trim() : "";
            if (head.StartsWith("ref: ", StringComparison.Ordinal))
            {
                string reference = Path.GetFullPath(Path.Combine(git, head[5..]));
                if (!reference.StartsWith(git + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
                if (File.Exists(reference)) hash.AppendData(SHA256.HashData(File.ReadAllBytes(reference)));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());

        void Add(string value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }
    }
}
