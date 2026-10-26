using System.Text.Json;

namespace Maf.Handoff.Core;

public sealed record GitLabMergeRequestReview(int Iid, string Url, string Title, string SourceBranch,
    string TargetBranch, string Context);

public sealed partial class GitLabMergeRequestClient
{
    public async Task<GitLabMergeRequestReview> ReadReviewAsync(string repositoryUrl, string token,
        int iid, bool includeResolved, CancellationToken cancellationToken)
    {
        if (iid < 1) throw new ArgumentException("A positive GitLab MR number is required.");
        if (string.IsNullOrWhiteSpace(token)) throw new RepositoryException("A GitLab token is required to read MR reviews.");
        (Uri origin, string project) = ParseRepository(repositoryUrl);
        string path = $"api/v4/projects/{Uri.EscapeDataString(project)}/merge_requests/{iid}";
        using JsonDocument mr = await GetAsync(path);
        JsonElement root = mr.RootElement;
        if (root.GetProperty("state").GetString() != "opened")
            throw new RepositoryException("Review runs require an open GitLab merge request.");
        if (root.GetProperty("source_project_id").GetInt64() != root.GetProperty("target_project_id").GetInt64())
            throw new RepositoryException("Review runs currently require the MR source branch to be in the same project (fork MRs are unsupported).");
        string source = root.GetProperty("source_branch").GetString()!;
        string target = root.GetProperty("target_branch").GetString()!;
        RepositoryWorkspace.ValidateBranchName(source);
        string title = root.GetProperty("title").GetString() ?? "";
        string url = root.GetProperty("web_url").GetString() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? webUri) || webUri.Scheme != "https" ||
            webUri.Authority != origin.Authority || !string.IsNullOrEmpty(webUri.UserInfo))
            throw new RepositoryException("GitLab returned an invalid MR URL.");

        List<JsonElement> discussions = [];
        for (int page = 1; ; page++)
        {
            if (page > 100) throw new RepositoryException("MR discussion limit exceeded; narrow the review before starting a run.");
            using JsonDocument batch = await GetAsync($"{path}/discussions?per_page=100&page={page}");
            foreach (JsonElement discussion in batch.RootElement.EnumerateArray())
            {
                JsonElement[] notes = discussion.GetProperty("notes").EnumerateArray()
                    .Where(note => !Flag(note, "system")).ToArray();
                bool resolved = notes.Any(note => Flag(note, "resolvable")) &&
                    notes.Where(note => Flag(note, "resolvable")).All(note => Flag(note, "resolved"));
                if (notes.Length == 0 || resolved && !includeResolved) continue;
                // Keep complete threads, including replies, author, timestamps, position and resolution metadata.
                discussions.Add(discussion.Clone());
            }
            if (batch.RootElement.GetArrayLength() < 100) break;
        }
        string context = JsonSerializer.Serialize(new
        {
            iid, url, title, sourceBranch = source, targetBranch = target,
            description = root.GetProperty("description").GetString(), includeResolved,
            discussions,
        });
        if (context.Length > 200_000)
            throw new RepositoryException("MR review context exceeds 200,000 characters; reduce the discussion scope before starting a run.");
        return new(iid, url, title, source, target, context);

        async Task<JsonDocument> GetAsync(string relativePath)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(origin, relativePath));
            request.Headers.Add("PRIVATE-TOKEN", token);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new RepositoryException($"GitLab MR review read failed (HTTP {(int)response.StatusCode}). Check project, MR number and token permissions.");
            try
            {
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            }
            catch (JsonException)
            {
                throw new RepositoryException("GitLab returned invalid JSON for the MR review.");
            }
        }
    }

    private static bool Flag(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
}
