using System.Net.Http.Json;
using System.Text.Json;

namespace Maf.Handoff.Core;

public sealed class GitLabMergeRequestClient(HttpClient httpClient)
{
    public async Task<PullRequestResult> CreateAsync(
        string repositoryUrl,
        string gitLabToken,
        string title,
        string headBranch,
        string baseBranch,
        string body,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gitLabToken))
        {
            throw new RepositoryException("A GitLab token is required to create a merge request.");
        }

        (Uri origin, string projectPath) = ParseRepository(repositoryUrl);
        string encodedProject = Uri.EscapeDataString(projectPath);
        Uri endpoint = new(origin, $"api/v4/projects/{encodedProject}/merge_requests");
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                source_branch = headBranch,
                target_branch = baseBranch,
                title,
                description = body,
            }),
        };
        request.Headers.Add("PRIVATE-TOKEN", gitLabToken);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new RepositoryException(
                $"GitLab merge request creation failed ({(int)response.StatusCode}): {ReadError(responseBody)}");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            int number = document.RootElement.GetProperty("iid").GetInt32();
            string url = document.RootElement.GetProperty("web_url").GetString() ?? string.Empty;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? mergeRequestUri) ||
                !mergeRequestUri.Host.Equals(origin.Host, StringComparison.OrdinalIgnoreCase))
            {
                throw new JsonException();
            }

            return new(number, url);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException)
        {
            throw new RepositoryException("GitLab returned an invalid merge request response.");
        }
    }

    private static (Uri Origin, string ProjectPath) ParseRepository(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new RepositoryException("Repository URL must be a valid HTTPS GitLab URL.");
        }

        string path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        if (path.Split('/').Length < 2)
        {
            throw new RepositoryException("Repository URL must identify a GitLab namespace and project.");
        }

        return (new Uri($"{uri.Scheme}://{uri.Authority}/"), path);
    }

    private static string ReadError(string content)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("message", out JsonElement message))
            {
                string text = message.ValueKind == JsonValueKind.String
                    ? message.GetString() ?? string.Empty
                    : message.ToString();
                return Limit(text);
            }
        }
        catch (JsonException)
        {
        }

        return "GitLab rejected the request.";
    }

    private static string Limit(string value) => value.Length <= 500 ? value : value[..500] + "...";
}
