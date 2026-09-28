using System.Net;
using System.Text;
using System.Text.Json;
using Maf.Handoff.Api;

namespace Maf.Handoff.Tests;

public sealed class CogniaDocumentSearchServiceTests
{
    [Fact]
    public async Task AuthenticatesAndSearchesConfiguredKnowledgeBases()
    {
        RecordingHandler handler = new();
        CogniaDocumentSearchService service = new(new TestHttpClientFactory(handler));
        CogniaSearchConfiguration configuration = new(
            "https://cognia.example.test/",
            "client-one",
            "secret-one",
            [12, 27]);

        string result = await service.CreateSearch(configuration).SearchAsync("deployment policy", 4);

        Assert.Equal(2, handler.Requests.Count);
        RecordedRequest authentication = handler.Requests[0];
        Assert.Equal("/api/access/client-auth/token", authentication.Path);
        Assert.Contains("\"clientId\":\"client-one\"", authentication.Body, StringComparison.Ordinal);
        Assert.Contains("\"clientSecret\":\"secret-one\"", authentication.Body, StringComparison.Ordinal);

        RecordedRequest search = handler.Requests[1];
        Assert.Equal("/api/engine/search", search.Path);
        Assert.Equal("Bearer machine-token", search.Authorization);
        using JsonDocument payload = JsonDocument.Parse(search.Body);
        Assert.Equal("deployment policy", payload.RootElement.GetProperty("query").GetString());
        Assert.Equal([12L, 27L], payload.RootElement.GetProperty("knowledgeBaseIds").EnumerateArray().Select(value => value.GetInt64()));
        Assert.Equal(4, payload.RootElement.GetProperty("limit").GetInt32());
        Assert.Contains("Cognia knowledge base 12, knowledge 101, revision 3, chunk 2", result, StringComparison.Ordinal);
        Assert.Contains("Use managed identity for production deployments.", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMissingKnowledgeBaseIds()
    {
        CogniaDocumentSearchService service = new(new TestHttpClientFactory(new RecordingHandler()));
        CogniaSearchConfiguration configuration = new(
            "https://cognia.example.test",
            "client-one",
            "secret-one",
            []);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => service.CreateSearch(configuration));

        Assert.Contains("knowledge base ID", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(),
                body));

            string json = request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                ? """{"tokenType":"Bearer","accessToken":"machine-token","expiresIn":"300"}"""
                : """
                  {
                    "items": [
                      {
                        "rank": 1,
                        "relevanceScore": "0.94",
                        "knowledgeBaseId": "12",
                        "knowledgeId": "101",
                        "revisionId": "301",
                        "revisionNumber": "3",
                        "chunkId": "501",
                        "chunkIndex": "1",
                        "chunkText": "Use managed identity for production deployments."
                      }
                    ],
                    "nextContinuationToken": null
                  }
                  """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record RecordedRequest(string Path, string? Authorization, string Body);
}
