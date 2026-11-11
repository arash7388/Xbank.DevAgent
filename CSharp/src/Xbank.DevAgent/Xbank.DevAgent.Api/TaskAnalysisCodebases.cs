using System.Text.Json;
using Xbank.DevAgent.Core;

namespace Xbank.DevAgent.Api;

public sealed record TaskAnalysisRepository(string Component, string Url, string Token, string Host);

// Owns only a unique temporary directory, never a user's configured checkout.
public sealed class TaskAnalysisCodebases : IDisposable
{
    public Dictionary<string, RepositoryWorkspace> Repositories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Notices { get; } = [];
    public string RootPath { get; } = Path.Combine(Path.GetTempPath(), "xbank-task-analysis", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (!Directory.Exists(RootPath)) return;
        try
        {
            foreach (string file in Directory.EnumerateFiles(RootPath, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(RootPath, recursive: true);
        }
        catch (IOException) { /* A locked checkout must not hide the analysis result or original failure. */ }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class TaskAnalysisCodebaseLoader(IConfiguration configuration,
    Func<TaskAnalysisRepository, string, CancellationToken, Task<RepositoryWorkspace>>? clone = null)
{
    public IReadOnlyList<TaskAnalysisRepository> Resolve(JsonElement issue, List<string> notices)
    {
        if (!issue.TryGetProperty("fields", out var fields) || !fields.TryGetProperty("components", out var components)
            || components.ValueKind != JsonValueKind.Array)
        {
            notices.Add("No xbank.* Jira components were found; related codebase inspection was unavailable.");
            return [];
        }
        string[] names = components.EnumerateArray()
            .Select(component => component.ValueKind == JsonValueKind.Object && component.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String ? name.GetString()?.Trim() : null)
            .OfType<string>().Where(name => name.StartsWith("xbank.", StringComparison.OrdinalIgnoreCase) && name.Length > 6)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0) notices.Add("No xbank.* Jira components were found; related codebase inspection was unavailable.");
        List<TaskAnalysisRepository> result = [];
        foreach (string name in names)
        {
            var matches = DeveloperSettingsStore.Projects(configuration).Where(project =>
                name.Equals(project["Key"] ?? project.Key, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(ProjectName(project["GitLabProjectPath"]), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
            {
                notices.Add($"{name}: {(matches.Length == 0 ? "no configured repository matches this Jira component" : "multiple configured repositories match this Jira component")}. Check repository names and GitLab URLs in Settings.");
                continue;
            }
            string url = matches[0]["GitLabProjectPath"] ?? "";
            string host = configuration["GitLab:Host"] ?? "";
            string token = DeveloperSettingsStore.GitLabToken(configuration, url) ?? "";
            try { RepositoryWorkspace.ValidateRepositoryUrl(url, RepositoryProvider.GitLab, host); }
            catch (RepositoryException)
            {
                notices.Add($"{name}: configure a valid repository URL and matching GitLab host in Settings.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(token))
            {
                notices.Add($"{name}: no GitLab PAT is configured. Save a repository token or shared GitLab token in Settings.");
                continue;
            }
            result.Add(new(name, url, token, host));
        }
        return result;
    }

    public async Task<TaskAnalysisCodebases> LoadAsync(JsonElement issue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskAnalysisCodebases result = new();
        try
        {
            foreach (var repository in Resolve(issue, result.Notices))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var workspace = clone is not null ? await clone(repository, result.RootPath, cancellationToken)
                        : await RepositoryWorkspace.CloneDefaultBranchAsync(repository.Url, repository.Token, result.RootPath,
                            cancellationToken, repository.Host);
                    result.Repositories.Add(repository.Component, workspace);
                }
                catch (Exception exception) when (exception is RepositoryException or IOException or System.ComponentModel.Win32Exception)
                {
                    // Git/process errors may contain credentials or machine paths; do not send them to the model or browser.
                    result.Notices.Add($"{repository.Component}: could not fetch the GitLab codebase. Check the PAT, repository permissions, GitLab connectivity, and default branch.");
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static string? ProjectName(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        string name = Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/').Last());
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
