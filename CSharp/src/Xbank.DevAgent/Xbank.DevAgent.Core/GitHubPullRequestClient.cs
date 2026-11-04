using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Xbank.DevAgent.Core;

public sealed class GitHubPullRequestClient(HttpClient httpClient)
{
    public async Task<PullRequestResult> CreateAsync(
        string repositoryUrl,
        string githubToken,
        string title,
        string headBranch,
        string baseBranch,
        string body,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(githubToken))
        {
            throw new RepositoryException("A GitHub token is required to create a pull request.");
        }

        (string owner, string repository) = ParseRepository(repositoryUrl);
        string endpoint = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/pulls";
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                title,
                head = headBranch,
                @base = baseBranch,
                body,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string message = ReadErrorMessage(responseBody);
            throw new RepositoryException(
                $"GitHub pull request creation failed ({(int)response.StatusCode}): {message}");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            int number = document.RootElement.GetProperty("number").GetInt32();
            string url = document.RootElement.GetProperty("html_url").GetString()
                ?? throw new JsonException("The pull request URL was absent.");
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? pullRequestUri) ||
                pullRequestUri.Scheme != Uri.UriSchemeHttps ||
                !pullRequestUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new JsonException("The pull request URL was invalid.");
            }

            return new(number, pullRequestUri.AbsoluteUri);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new RepositoryException("GitHub returned an invalid pull request response.");
        }
    }

    private static (string Owner, string Repository) ParseRepository(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new RepositoryException("Repository URL must be an HTTPS github.com URL.");
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2 || segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new RepositoryException("Repository URL must identify one owner and repository.");
        }

        string repository = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];
        return (segments[0], repository);
    }

    private static string ReadErrorMessage(string responseBody)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            string? message = document.RootElement.TryGetProperty("message", out JsonElement value)
                ? value.GetString()
                : null;
            return string.IsNullOrWhiteSpace(message) ? "GitHub rejected the request." : Limit(message);
        }
        catch (JsonException)
        {
            return "GitHub rejected the request.";
        }
    }

    private static string Limit(string value) => value.Length <= 500 ? value : value[..500];
}

public sealed record PullRequestResult(int Number, string Url);
