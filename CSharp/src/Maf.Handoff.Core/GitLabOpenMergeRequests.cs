using System.Text.Json;

namespace Maf.Handoff.Core;

public sealed record GitLabOpenMergeRequest(int Iid, string Title, string Url, string SourceBranch,
    string TargetBranch, string Author, bool IsFork);

public sealed partial class GitLabMergeRequestClient
{
    public async Task<IReadOnlyList<GitLabOpenMergeRequest>> ListOpenAsync(string repositoryUrl,
        string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new RepositoryException("A GitLab token is required to list merge requests.");
        (Uri origin, string project) = ParseRepository(repositoryUrl);
        List<GitLabOpenMergeRequest> results = [];
        for (int page = 1; ; page++)
        {
            if (page > 100) throw new RepositoryException("Too many open merge requests to list.");
            string path = $"api/v4/projects/{Uri.EscapeDataString(project)}/merge_requests?state=opened&scope=all&order_by=updated_at&sort=desc&per_page=100&page={page}";
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(origin, path));
            request.Headers.Add("PRIVATE-TOKEN", token);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new RepositoryException($"Could not load GitLab merge requests (HTTP {(int)response.StatusCode}). Check the repository and token permissions.");
            try
            {
                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                foreach (JsonElement mr in document.RootElement.EnumerateArray())
                {
                    if (mr.GetProperty("state").GetString() != "opened") continue;
                    string url = mr.GetProperty("web_url").GetString() ?? "";
                    if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" ||
                        uri.Authority != origin.Authority || !string.IsNullOrEmpty(uri.UserInfo))
                        throw new RepositoryException("GitLab returned an invalid MR URL.");
                    results.Add(new(mr.GetProperty("iid").GetInt32(), mr.GetProperty("title").GetString() ?? "",
                        url, mr.GetProperty("source_branch").GetString() ?? "", mr.GetProperty("target_branch").GetString() ?? "",
                        mr.GetProperty("author").GetProperty("username").GetString() ?? "",
                        mr.GetProperty("source_project_id").GetInt64() != mr.GetProperty("target_project_id").GetInt64()));
                }
                if (document.RootElement.GetArrayLength() < 100) return results;
            }
            catch (JsonException) { throw new RepositoryException("GitLab returned invalid JSON for merge requests."); }
        }
    }
}
