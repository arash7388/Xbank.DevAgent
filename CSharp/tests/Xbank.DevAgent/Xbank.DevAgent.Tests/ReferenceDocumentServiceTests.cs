using System.Net;
using System.Text;
using System.Text.Json;
using Xbank.DevAgent.Api;
using Xbank.DevAgent.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Xbank.DevAgent.Tests;

public sealed class ReferenceDocumentServiceTests
{
    [Theory]
    [InlineData("\"PAYMENT TIMEOUT\"")]
    [InlineData("payment timeout")]
    [InlineData("payments.md")]
    public async Task PlainTextUploadSearchAndCleanupNeedNoExternalServices(string query)
    {
        ReferenceDocumentService service = new(new NoNetworkFactory(), Options.Create(new RagOptions()));
        ReferenceCollectionInfo collection = await service.CreateAsync(
            Files(), "openai", "", "not a URL", "", CancellationToken.None);
        try
        {
            Assert.True(service.Exists(collection.Id));
            Assert.Equal(2, collection.FileCount);
            string result = await service.GetSearch(collection.Id).SearchAsync(query, 1);
            Assert.Contains("[Search method: exact keyword]", result);
            Assert.Contains("payments.md, chunk 1", result);
            Assert.Contains("Payment timeout must be thirty seconds.", result);
            Assert.DoesNotContain("gardening.md", result);

            string missing = await service.GetSearch(collection.Id).SearchAsync("missing phrase", 4);
            Assert.Contains("No relevant attached-document excerpts", missing);
        }
        finally
        {
            await service.DeleteAsync(collection.Id);
        }
        Assert.False(service.Exists(collection.Id));
        Assert.Throws<ArgumentException>(() => service.GetSearch(collection.Id));
    }

    [Fact]
    public async Task SemanticModeStillEmbedsIndexesQueriesAndDeletes()
    {
        SemanticHandler handler = new();
        ReferenceDocumentService service = new(new TestHttpClientFactory(handler), Options.Create(new RagOptions()));
        ReferenceCollectionInfo collection = await service.CreateAsync(
            Files(), "ollama", "", "http://127.0.0.1:6333", "", CancellationToken.None, "semantic");
        try
        {
            Assert.Equal(1, handler.EmbeddingRequests);
            Assert.Equal(1, handler.Upserts);
            string exact = await service.GetSearch(collection.Id).SearchAsync("\"payment timeout\"", 1);
            Assert.Contains("[Search method: exact keyword]", exact);
            Assert.Equal(1, handler.EmbeddingRequests);

            string semantic = await service.GetSearch(collection.Id).SearchAsync("How long should a transaction wait?", 1);
            Assert.Contains("[Search method: semantic]", semantic);
            Assert.Contains("payments.md, chunk 1", semantic);
            Assert.Equal(2, handler.EmbeddingRequests);
            Assert.Equal(1, handler.Queries);
        }
        finally
        {
            await service.DeleteAsync(collection.Id);
        }
        Assert.Equal(1, handler.Deletes);
    }

    [Fact]
    public async Task RejectsUnknownSearchMode()
    {
        ReferenceDocumentService service = new(new NoNetworkFactory(), Options.Create(new RagOptions()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            Files(), "", "", "", "", CancellationToken.None, "unknown"));
    }

    private static FormFileCollection Files() =>
    [
        File("payments.md", "Payment timeout must be thirty seconds."),
        File("gardening.md", "Water the flowers every morning."),
    ];

    private static FormFile File(string name, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return new(new MemoryStream(bytes), 0, bytes.Length, "files", name);
    }

    private sealed class NoNetworkFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Plain text mode must not create HTTP clients.");
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SemanticHandler : HttpMessageHandler
    {
        public int EmbeddingRequests { get; private set; }
        public int Upserts { get; private set; }
        public int Queries { get; private set; }
        public int Deletes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string json = """{"result":true,"status":"ok"}""";
            if (path == "/api/embed")
            {
                EmbeddingRequests++;
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                int count = body.RootElement.GetProperty("input").GetArrayLength();
                json = JsonSerializer.Serialize(new { embeddings = Enumerable.Repeat(new[] { 1f, 0f }, count) });
            }
            else if (path.EndsWith("/points/query", StringComparison.Ordinal))
            {
                Queries++;
                json = """{"result":{"points":[{"payload":{"file":"payments.md","chunk":0,"text":"Payment timeout must be thirty seconds."}}]}}""";
            }
            else if (path.EndsWith("/points", StringComparison.Ordinal))
            {
                Upserts++;
            }
            else if (request.Method == HttpMethod.Delete)
            {
                Deletes++;
            }
            else
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal(2, body.RootElement.GetProperty("vectors").GetProperty("size").GetInt32());
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
