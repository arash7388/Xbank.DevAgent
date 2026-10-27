using System.Net;
using System.Text;
using System.Text.Json;
using Maf.Handoff.Core;

namespace Maf.Handoff.Tests;

public sealed class GitLabMergeRequestClientTests
{
    private const string ReviewMr = """{"iid":17,"state":"opened","title":"Fix checkout","description":"Expected behavior","source_project_id":1,"target_project_id":1,"source_branch":"user/WEPOD-123","target_branch":"main","web_url":"https://gitlab.com/acme/orders/-/merge_requests/17"}""";

    [Fact]
    public async Task ListsOpenMergeRequestsAcrossPagesAndMarksForks()
    {
        int calls = 0;
        string item = ReviewMr.Replace("\"description\":", "\"author\":{\"username\":\"alice\"},\"description\":", StringComparison.Ordinal);
        using HttpClient http = new(new StubHandler(request =>
        {
            calls++;
            Assert.Contains("/projects/acme%2Forders/merge_requests", request.RequestUri!.AbsoluteUri);
            Assert.Contains("state=opened", request.RequestUri.Query);
            Assert.Contains($"page={calls}", request.RequestUri.Query);
            Assert.Equal("token", Assert.Single(request.Headers.GetValues("PRIVATE-TOKEN")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(calls == 1
                    ? "[" + string.Join(',', Enumerable.Repeat(item, 100)) + "]"
                    : "[" + item.Replace("\"source_project_id\":1", "\"source_project_id\":2", StringComparison.Ordinal) + "]"),
            });
        }));
        var result = await new GitLabMergeRequestClient(http).ListOpenAsync(
            "https://gitlab.com/acme/orders.git", "token", CancellationToken.None);
        Assert.Equal(101, result.Count);
        Assert.Equal(2, calls);
        Assert.False(result[0].IsFork);
        Assert.True(result[^1].IsFork);
        Assert.Equal("alice", result[0].Author);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsReviewThreadsWithRepliesAndPositions(bool includeResolved)
    {
        using HttpClient http = new(new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("token", Assert.Single(request.Headers.GetValues("PRIVATE-TOKEN")));
            string body = request.RequestUri!.Query.Length == 0 ? ReviewMr : """
                [
                  {"id":"open","notes":[{"id":1,"body":"Handle null","author":{"username":"reviewer"},"position":{"new_path":"Checkout.cs","new_line":12},"resolvable":true,"resolved":false},{"id":2,"body":"Agreed","author":{"username":"developer"}}]},
                  {"id":"done","notes":[{"body":"Already fixed","resolvable":true,"resolved":true}]},
                  {"id":"general","notes":[{"body":"Check tests","system":false}]},
                  {"id":"system","notes":[{"body":"pushed a commit","system":true}]}
                ]
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }));
        GitLabMergeRequestReview result = await new GitLabMergeRequestClient(http).ReadReviewAsync(
            "https://gitlab.com/acme/orders.git", "token", 17, includeResolved, CancellationToken.None);
        Assert.Equal("user/WEPOD-123", result.SourceBranch);
        Assert.Contains("Checkout.cs", result.Context);
        Assert.Contains("Agreed", result.Context);
        Assert.Contains("Check tests", result.Context);
        Assert.DoesNotContain("pushed a commit", result.Context);
        Assert.Equal(includeResolved, result.Context.Contains("Already fixed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IndependentReviewDoesNotFetchOrIncludeDiscussions()
    {
        int requests = 0;
        using HttpClient http = new(new StubHandler(request =>
        {
            requests++;
            Assert.DoesNotContain("discussions", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReviewMr) });
        }));
        GitLabMergeRequestReview review = await new GitLabMergeRequestClient(http).ReadReviewAsync(
            "https://gitlab.com/acme/orders.git", "token", 17, true, CancellationToken.None,
            MergeRequestReviewModes.Independent);
        Assert.Equal(1, requests);
        using JsonDocument context = JsonDocument.Parse(review.Context);
        Assert.Equal("Expected behavior", context.RootElement.GetProperty("description").GetString());
        Assert.Empty(context.RootElement.GetProperty("discussions").EnumerateArray());
        Assert.False(context.RootElement.GetProperty("includeResolved").GetBoolean());
    }

    [Fact]
    public async Task ReadsEveryDiscussionPage()
    {
        int requests = 0;
        string fullPage = "[" + string.Join(',', Enumerable.Repeat("""{"id":"thread","notes":[{"body":"feedback"}]}""", 100)) + "]";
        using HttpClient http = new(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(++requests switch { 1 => ReviewMr, 2 => fullPage, _ => "[]" }),
        })));
        await new GitLabMergeRequestClient(http).ReadReviewAsync("https://gitlab.com/acme/orders.git", "token", 17, false, CancellationToken.None);
        Assert.Equal(3, requests);
    }

    [Theory]
    [InlineData("\"opened\"", "\"merged\"")]
    [InlineData("\"source_project_id\":1", "\"source_project_id\":2")]
    public async Task RejectsClosedOrForkMergeRequests(string original, string replacement)
    {
        using HttpClient http = new(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ReviewMr.Replace(original, replacement, StringComparison.Ordinal)) })));
        await Assert.ThrowsAsync<RepositoryException>(() => new GitLabMergeRequestClient(http).ReadReviewAsync(
            "https://gitlab.com/acme/orders.git", "token", 17, false, CancellationToken.None));
    }

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
