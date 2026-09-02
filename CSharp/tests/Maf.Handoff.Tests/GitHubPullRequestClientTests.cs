using System.Net;
using System.Text;
using System.Text.Json;
using Maf.Handoff.Core;

namespace Maf.Handoff.Tests;

public sealed class GitHubPullRequestClientTests
{
    [Fact]
    public async Task CreatesPullRequestWithExpectedGitHubRequestAsync()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        using HttpClient httpClient = new(new StubHandler(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    """{"number":42,"html_url":"https://github.com/arash7388/JiraCommenter/pull/42"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }))
        {
            BaseAddress = new Uri("https://api.github.com/"),
        };
        GitHubPullRequestClient client = new(httpClient);

        PullRequestResult result = await client.CreateAsync(
            "https://github.com/arash7388/JiraCommenter.git",
            "secret-token",
            "Update project key",
            "agent/change-1",
            "master",
            "Automated change.",
            CancellationToken.None);

        Assert.Equal(42, result.Number);
        Assert.Equal("https://github.com/arash7388/JiraCommenter/pull/42", result.Url);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://api.github.com/repos/arash7388/JiraCommenter/pulls", capturedRequest.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization!.Scheme);
        Assert.Equal("secret-token", capturedRequest.Headers.Authorization.Parameter);
        Assert.Contains("application/vnd.github+json", capturedRequest.Headers.Accept.Select(value => value.MediaType));
        Assert.Equal("2026-03-10", Assert.Single(capturedRequest.Headers.GetValues("X-GitHub-Api-Version")));

        using JsonDocument payload = JsonDocument.Parse(capturedBody!);
        Assert.Equal("Update project key", payload.RootElement.GetProperty("title").GetString());
        Assert.Equal("agent/change-1", payload.RootElement.GetProperty("head").GetString());
        Assert.Equal("master", payload.RootElement.GetProperty("base").GetString());
    }

    [Fact]
    public async Task ReportsGitHubErrorWithoutExposingTokenAsync()
    {
        using HttpClient httpClient = new(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("""{"message":"Validation Failed"}"""),
        })))
        {
            BaseAddress = new Uri("https://api.github.com/"),
        };
        GitHubPullRequestClient client = new(httpClient);

        RepositoryException exception = await Assert.ThrowsAsync<RepositoryException>(() => client.CreateAsync(
            "https://github.com/arash7388/JiraCommenter.git",
            "secret-token",
            "Update project key",
            "agent/change-1",
            "master",
            "Automated change.",
            CancellationToken.None));

        Assert.Contains("Validation Failed", exception.Message);
        Assert.DoesNotContain("secret-token", exception.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responseFactory(request);
    }
}
