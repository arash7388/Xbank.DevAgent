using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maf.Handoff.Api;
using Maf.Handoff.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Maf.Handoff.Tests;

// Exercises the actual routing, JSON binding, middleware, DI and SQLite boundary.
// Configuration and storage are isolated; HTTP dependencies fail closed.
public sealed class ApiContractTests(ApiContractTests.ApiFactory factory) : IClassFixture<ApiContractTests.ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/agents/default-prompts")]
    [InlineData("/api/agents/tools")]
    [InlineData("/api/projects")]
    [InlineData("/api/providers/configured")]
    [InlineData("/api/jira/settings")]
    [InlineData("/api/codegraph/config")]
    [InlineData("/api/codegraph/index/status")]
    [InlineData("/api/runs")]
    public async Task LocalReadEndpointsReturnJsonWithoutSecrets(string route)
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument parsed = JsonDocument.Parse(body);
        Assert.DoesNotContain(ApiFactory.Secret, body);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/app.js")]
    [InlineData("/styles.css")]
    public async Task WorkbenchAssetsAreServedWithNoCache(string route)
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("GET", "/api/sessions/{id}")]
    [InlineData("GET", "/api/sessions/{id}/logs")]
    [InlineData("GET", "/api/sessions/{id}/report")]
    [InlineData("GET", "/api/runs/{id}")]
    [InlineData("POST", "/api/sessions/{id}/cancel")]
    [InlineData("POST", "/api/sessions/{id}/reject")]
    [InlineData("POST", "/api/sessions/{id}/pull-request")]
    [InlineData("POST", "/api/sessions/{id}/approve")]
    [InlineData("POST", "/api/sessions/{id}/clarify")]
    [InlineData("POST", "/api/sessions/{id}/modify")]
    public async Task MissingRunsHaveConsistentNotFoundResponses(string method, string route)
    {
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(new HttpMethod(method), route.Replace("{id}", Guid.NewGuid().ToString()))
        {
            Content = method == "POST" ? JsonContent.Create(new { branch = "test", commitMessage = "test", push = false,
                clarificationId = Guid.NewGuid(), answer = "test", prompt = "test" }) : null,
        };
        using HttpResponseMessage response = await client.SendAsync(request);
        await AssertError(response, HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("autonomyLevel", "-1")]
    [InlineData("autonomyLevel", "26")]
    [InlineData("autonomyLevel", "101")]
    [InlineData("maxAiCalls", "0")]
    [InlineData("maxAiCalls", "-1")]
    [InlineData("maxBudgetUsd", "0.009")]
    [InlineData("maxBudgetUsd", "1000000.01")]
    [InlineData("repositoryUrl", "\"\"")]
    [InlineData("baseBranch", "\" \"")]
    [InlineData("task", "\"\"")]
    [InlineData("task", "\"No issue number\"")]
    [InlineData("task", "\"WEPOD-1 and WEPOD-2\"")]
    [InlineData("agents", "{}")]
    [InlineData("agents", "null")]
    [InlineData("sourceControl", "\"svn\"")]
    [InlineData("autoPush", "true")]
    [InlineData("autoCreatePullRequest", "true")]
    [InlineData("referenceCollectionId", "\"00000000-0000-0000-0000-000000000001\"")]
    [InlineData("jiraIssueKey", "\"invalid\"")]
    [InlineData("jiraAttachmentIds", "[\"1\"]")]
    public async Task InvalidSessionInputIsRejectedBeforeAnyRunIsStored(string property, string value)
    {
        JsonObject input = ValidRequest();
        input[property] = JsonNode.Parse(value);
        await AssertInvalidSession(input);
    }

    [Theory]
    [InlineData("planner")]
    [InlineData("developer")]
    [InlineData("reviewer")]
    [InlineData("release_manager")]
    public async Task EachRequiredAgentMustBePresent(string agent)
    {
        JsonObject input = ValidRequest();
        input["agents"]!.AsObject().Remove(agent);
        await AssertInvalidSession(input);
    }

    [Theory]
    [InlineData("planner")]
    [InlineData("developer")]
    [InlineData("reviewer")]
    [InlineData("release_manager")]
    public async Task NullAgentConfigurationsAreValidationErrors(string agent)
    {
        JsonObject input = ValidRequest();
        input["agents"]![agent] = null;
        await AssertInvalidSession(input);
    }

    [Fact]
    public async Task OversizedTaskIsRejected()
    {
        JsonObject input = ValidRequest();
        input["task"] = "WEPOD-42 " + new string('x', 20_000);
        await AssertInvalidSession(input);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{broken")]
    public async Task MalformedSessionBodiesReturnBadRequest(string json)
    {
        using HttpClient client = factory.CreateClient();
        using StringContent content = new(json, System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("/api/sessions", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProviderValidationNormalizesTheWireResponse()
    {
        using HttpClient client = factory.CreateClient();
        ModelProviderDefinition provider = Provider() with { Id = " test-model ", Url = "https://example.test/v1/chat/completions/" };
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/providers/validate", provider);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ModelProviderDefinition normalized = (await response.Content.ReadFromJsonAsync<ModelProviderDefinition>())!;
        Assert.Equal("test-model", normalized.Id);
        Assert.Equal("https://example.test/v1", normalized.Url);
    }

    [Theory]
    [InlineData("toolCalling", "false")]
    [InlineData("maxInputTokens", "0")]
    [InlineData("maxOutputTokens", "2000001")]
    [InlineData("inputCostPerMillionTokens", "-0.01")]
    [InlineData("url", "\"http://example.test\"")]
    [InlineData("url", "\"https://user:secret@example.test\"")]
    [InlineData("reasoningEffort", "\"high\"")]
    public async Task InvalidProviderDefinitionsReturnStructuredBadRequest(string property, string value)
    {
        JsonObject input = JsonSerializer.SerializeToNode(Provider(), Json)!.AsObject();
        input[property] = JsonNode.Parse(value);
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/providers/validate", input);
        await AssertError(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PlainTextMultipartUploadCanBeSearchedWithoutExternalServices()
    {
        using HttpClient client = factory.CreateClient();
        using MultipartFormDataContent content = new();
        content.Add(new StringContent("exact"), "searchMode");
        content.Add(new StringContent("Payment timeout must be thirty seconds."), "files", "requirements.md");
        using HttpResponseMessage response = await client.PostAsync("/api/reference-collections", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        ReferenceCollectionInfo collection = (await response.Content.ReadFromJsonAsync<ReferenceCollectionInfo>())!;
        Assert.Equal($"/api/reference-collections/{collection.Id}", response.Headers.Location!.ToString());
        ReferenceDocumentService documents = factory.Services.GetRequiredService<ReferenceDocumentService>();
        try
        {
            Assert.Equal(1, collection.FileCount);
            Assert.Contains("thirty seconds", await documents.GetSearch(collection.Id).SearchAsync("Payment timeout", 5));
        }
        finally { await documents.DeleteAsync(collection.Id); }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unsupported")]
    [InlineData("invalid-mode")]
    [InlineData("non-multipart")]
    public async Task InvalidReferenceUploadsReturnBadRequest(string scenario)
    {
        using HttpClient client = factory.CreateClient();
        using MultipartFormDataContent multipart = new();
        if (scenario != "empty") multipart.Add(new StringContent("content"), "files",
            scenario == "unsupported" ? "image.exe" : "notes.md");
        multipart.Add(new StringContent(scenario == "invalid-mode" ? "unknown" : "exact"), "searchMode");
        using HttpContent content = scenario == "non-multipart" ? new StringContent("content") : multipart;
        using HttpResponseMessage response = await client.PostAsync("/api/reference-collections", content);
        await AssertError(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ArchivedSessionReturnsDurableStatusAndPatch()
    {
        Guid id = Guid.NewGuid();
        IWorkflowRunStore store = factory.Services.GetRequiredService<IWorkflowRunStore>();
        store.Create(id, "repository_change", "{}", DateTimeOffset.UtcNow);
        SessionSnapshot snapshot = new(id, "https://github.com/example/repository", "github", "main", "WEPOD-42",
            DateTimeOffset.UtcNow, "running", [], "preserved patch", null, null, null, null);
        store.SaveSnapshot(id, JsonSerializer.Serialize(snapshot));
        store.UpdateStatus(id, "failed", "Provider disconnected");
        using HttpClient client = factory.CreateClient();
        SessionSnapshot result = (await client.GetFromJsonAsync<SessionSnapshot>($"/api/sessions/{id}"))!;
        Assert.Equal(id, result.Id);
        Assert.Equal("failed", result.Status);
        Assert.Equal("Provider disconnected", result.Error);
        Assert.Equal("preserved patch", result.Diff);
    }

    private async Task AssertInvalidSession(JsonObject input)
    {
        using HttpClient client = factory.CreateClient();
        IWorkflowRunStore store = factory.Services.GetRequiredService<IWorkflowRunStore>();
        int before = store.ListRuns(100).Count;
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/sessions", input);
        await AssertError(response, HttpStatusCode.BadRequest);
        Assert.Equal(before, store.ListRuns(100).Count);
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode expected)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        using JsonDocument json = JsonDocument.Parse(body);
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("error").GetString()));
        Assert.DoesNotContain(ApiFactory.Secret, body);
    }

    private static ModelProviderDefinition Provider() => new("test-model", "Test", "https://example.test/v1",
        true, false, 8192, 2048, false, null, []);

    private static JsonObject ValidRequest() => JsonSerializer.SerializeToNode(new CreateSessionRequest(
        "https://github.com/example/repository", "main", "WEPOD-42 Update behavior",
        AgentNames.Required.ToDictionary(name => name, _ => new AgentModelConfiguration(Provider(), "test-key")),
        null, SourceControl: "github", CodeGraphEnabled: false), Json)!.AsObject();

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string Secret = "test-sentinel-must-not-appear-in-http-responses";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "maf-api-contract-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.Sources.Clear();
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Checkpointing:DatabasePath"] = Path.Combine(_root, "runs.db"),
                    ["CodeGraph:Enabled"] = "false",
                    ["CodeGraph:RootPath"] = _root,
                    ["CodeGraph:DatabasePath"] = Path.Combine(_root, "graph.db"),
                    ["Providers:DeepSeek:ApiKey"] = Secret,
                    ["Providers:AIFA:ApiKey"] = Secret,
                    ["GitLab:Token"] = Secret,
                    ["Jira:Token"] = Secret,
                    ["Logging:LogLevel:Default"] = "Warning",
                });
            });
            builder.ConfigureTestServices(services => services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = new NoNetworkHandler())));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_root))
            {
                Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
                Directory.Delete(_root, recursive: true);
            }
        }

        private sealed class NoNetworkHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException($"Unexpected outbound HTTP call in API contract tests: {request.Method} {request.RequestUri?.Host}");
        }
    }
}
