using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Maf.Handoff.Core;

internal sealed record RepositoryBuildResult(bool Succeeded, string Evidence);

public sealed partial class RepositoryWorkspace
{
    internal async Task<RepositoryBuildResult> BuildAsync(CancellationToken cancellationToken)
    {
        string[] files = RunGit(["ls-files", "--cached", "--others", "--exclude-standard"])
            .StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // This repository's active application lives under CSharp/.
        if (Directory.Exists(Path.Combine(RootPath, "CSharp")))
            files = files.Where(path => path.StartsWith("CSharp/", StringComparison.OrdinalIgnoreCase)).ToArray();
        string[] solutions = files.Where(path => Path.GetExtension(path) is ".sln" or ".slnx").ToArray();
        string[] targets = solutions.Concat(files.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            .Where(path => File.Exists(Path.Combine(RootPath, path)))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (targets.Length == 0)
            return new(true, "Build check skipped: no tracked or new .NET solution/project files were found.");

        StringBuilder evidence = new();
        foreach (string target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = ResolveSourcePath(target, mustExist: true);
            ProcessStartInfo start = new("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(fullPath)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string argument in new[] { "build", fullPath, "--nologo", "--verbosity", "minimal" })
                start.ArgumentList.Add(argument);
            evidence.AppendLine($"dotnet build {target}");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                using Process process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start dotnet.");
                Task<string> stdout = ReadBuildOutputAsync(process.StandardOutput);
                Task<string> stderr = ReadBuildOutputAsync(process.StandardError);
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    return new(false, evidence + await stdout.ConfigureAwait(false) + "\n" +
                        await stderr.ConfigureAwait(false) + "\nBuild timed out after five minutes.");
                }
                string output = await stdout.ConfigureAwait(false);
                string errors = await stderr.ConfigureAwait(false);
                evidence.AppendLine($"Exit code: {process.ExitCode}");
                if (process.ExitCode != 0) return new(false, evidence + output + "\n" + errors);
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
            {
                return new(false, evidence + $"Build could not run: {error.Message}");
            }
        }
        return new(true, evidence + "BUILD_PASSED");
    }

    private static async Task<string> ReadBuildOutputAsync(StreamReader reader)
    {
        const int limit = 24_000;
        StringBuilder output = new();
        char[] buffer = new char[4096];
        int count;
        bool truncated = false;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            int available = Math.Min(count, limit - output.Length);
            output.Append(buffer, 0, available);
            truncated |= available < count;
        }
        if (truncated) output.AppendLine("\n[Build output truncated]");
        return output.ToString();
    }
}
