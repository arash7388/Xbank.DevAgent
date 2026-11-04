using System.Security.Cryptography;
using System.Text;

namespace Xbank.DevAgent.Api;

public sealed record CodeGraphSourceFingerprint(string RootPath, string SourceRelativePath, string Hash)
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "TestResults", "Python",
    };

    private static readonly HashSet<string> InputExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".json", ".config",
        ".editorconfig", ".ruleset", ".resx", ".razor", ".cshtml", ".snk",
        ".projitems", ".shproj", ".rsp", ".xaml", ".tt", ".dll", ".winmd",
    };

    // Hash relative paths as well as bytes: additions, deletions and renames matter. Neither
    // timestamps nor the checkout's absolute location affect the fingerprint.
    public static CodeGraphSourceFingerprint Capture(string sourcePath, CancellationToken cancellationToken = default)
    {
        string source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("CodeGraph source was not found.", source);
        string root = FindRoot(source);
        string relativeSource = Path.GetRelativePath(root, source).Replace('\\', '/');
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AddText(hash, "maf-csharp-source-v1");
        AddText(hash, relativeSource);
        foreach (string file in EnumerateInputs(root, cancellationToken)
            .OrderBy(path => Path.GetRelativePath(root, path).Replace('\\', '/'), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddText(hash, Path.GetRelativePath(root, file).Replace('\\', '/'));
            using FileStream stream = File.OpenRead(file);
            hash.AppendData(SHA256.HashData(stream));
        }
        return new(root, relativeSource, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void AddText(IncrementalHash hash, string text)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(text));
        hash.AppendData([0]);
    }

    private static string FindRoot(string source)
    {
        DirectoryInfo directory = new(Path.GetDirectoryName(source)!);
        for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
        return directory.FullName;
    }

    private static IEnumerable<string> EnumerateInputs(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (string file in Directory.EnumerateFiles(directory))
            if (InputExtensions.Contains(Path.GetExtension(file)) ||
                Path.GetFileName(file).Equals(".editorconfig", StringComparison.OrdinalIgnoreCase))
                yield return file;
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            if (ExcludedDirectories.Contains(Path.GetFileName(child))) continue;
            // Do not follow junctions/symlinks outside the codebase or into cycles.
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (string file in EnumerateInputs(child, cancellationToken)) yield return file;
        }
    }
}
