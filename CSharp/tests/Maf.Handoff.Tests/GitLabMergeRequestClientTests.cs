using System.Net;
using System.Text;
using System.Text.Json;
using Maf.Handoff.Core;

namespace Maf.Handoff.Tests;

public sealed class GitLabMergeRequestClientTests
{
    [Fact]
    public async Task CreatesMergeRequestForNestedGitLabProjectAsync()
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
                    """{"iid":17,"web_url":"https://gitlab.com/acme/platform/orders/-/merge_requests/17"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }));
        GitLabMergeRequestClient client = new(httpClient);

        PullRequestResult result = await client.CreateAsync(
            "https://gitlab.com/acme/platform/orders.git",
            "secret-token",
            "Update project key",
            "agent/change-1",
            "main",
            "Automated change.",
            CancellationToken.None);

        Assert.Equal(17, result.Number);
        Assert.Equal("https://gitlab.com/acme/platform/orders/-/merge_requests/17", result.Url);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal(
            "https://gitlab.com/api/v4/projects/acme%2Fplatform%2Forders/merge_requests",
            capturedRequest.RequestUri!.AbsoluteUri);
        Assert.Equal("secret-token", Assert.Single(capturedRequest.Headers.GetValues("PRIVATE-TOKEN")));

        using JsonDocument payload = JsonDocument.Parse(capturedBody!);
        Assert.Equal("Update project key", payload.RootElement.GetProperty("title").GetString());
        Assert.Equal("agent/change-1", payload.RootElement.GetProperty("source_branch").GetString());
        Assert.Equal("main", payload.RootElement.GetProperty("target_branch").GetString());
    }

    [Fact]
    public async Task ReportsGitLabErrorWithoutExposingTokenAsync()
    {
        using HttpClient httpClient = new(new StubHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"message":"Source branch does not exist"}"""),
            })));
        GitLabMergeRequestClient client = new(httpClient);

        RepositoryException exception = await Assert.ThrowsAsync<RepositoryException>(() => client.CreateAsync(
            "https://gitlab.com/acme/orders.git",
            "secret-token",
            "Update project key",
            "agent/change-1",
            "main",
            "Automated change.",
            CancellationToken.None));

        Assert.Contains("Source branch does not exist", exception.Message);
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
