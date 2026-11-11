using System.Net;
using System.Text;
using System.Text.Json;
using Xbank.DevAgent.Core;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class AifaProviderTests
{
    [Fact]
    public async Task LogsAifaCurlBeforeFailureWithCredentialsRedactedAndBodyPreserved()
    {
        List<string> logs = [];
        const string body = "{\"model\":\"developer-model\",\"messages\":[{\"role\":\"user\",\"content\":\"what's $HOME?\"}]}";
        using HttpClient client = new(new AifaRequestHandler(new("123", "workflow", "request-1"),
            new CurlFailureHandler(body, logs), logs.Add));
        using HttpRequestMessage request = new(HttpMethod.Post, AifaRequestContext.Endpoint + "/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", "secret-token");
        request.Headers.Add("Cookie", "secret-cookie");
        request.Headers.Add("x-api-key", "secret-api-key");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        string log = Assert.Single(logs);
        Assert.Contains("curl --request 'POST' --url '" + AifaRequestContext.Endpoint + "/chat/completions'", log);
        Assert.Contains("Authorization: [REDACTED]", log);
        Assert.Contains("Cookie: [REDACTED]", log);
        Assert.Contains("x-api-key: [REDACTED]", log);
        Assert.DoesNotContain("secret-", log);
        Assert.Contains("x-request-id: request-1", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-session-id: workflow", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-user-id: 123", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Type: application/json; charset=utf-8", log);
        Assert.Contains("--data-raw '" + body.Replace("'", "'\"'\"'") + "'", log);

        using HttpResponseMessage other = await client.GetAsync("https://example.com/v1");
        Assert.Single(logs);
    }

    private sealed class CurlFailureHandler(string expectedBody, List<string> logs) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                Assert.Single(logs); // Diagnostic must exist before the provider returns an error.
                Assert.Equal(expectedBody, await request.Content.ReadAsStringAsync(cancellationToken));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task SendsCurlCompatibleRequestAndReadsToolCallsAndUsage()
    {
        RecordingHandler server = new();
        WorkflowSettings settings = new(AifaRequestContext.Endpoint, AifaRequestContext.Model, "test-token")
        {
            AifaContext = new("123", "beny", "beny"),
        };
        using IChatClient client = ModelChatClientFactory.Create(settings, server);
        ChatResponse response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal($"{AifaRequestContext.Endpoint}/chat/completions", server.Url);
        Assert.Equal("/v1/chat/completions", new Uri(server.Url!).AbsolutePath);
        Assert.Equal("Bearer test-token", server.Authorization);
        Assert.Equal("application/json", server.ContentType);
        Assert.Equal("beny", server.Headers["x-request-id"]);
        Assert.Equal("beny", server.Headers["x-session-id"]);
        Assert.Equal("123", server.Headers["x-user-id"]);
        using JsonDocument body = JsonDocument.Parse(server.Body!);
        Assert.Equal("developer-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.True(body.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("low", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Contains(response.Messages.SelectMany(message => message.Contents), content => content is FunctionCallContent);
        Assert.Equal(10, response.Usage!.InputTokenCount);
        Assert.Equal(3, response.Usage.OutputTokenCount);
    }

    [Fact]
    public async Task GeneratesDistinctRequestIdsWithStableSessionAndScopesHeadersToAifa()
    {
        RecordingHandler server = new();
        using HttpClient client = new(new AifaRequestHandler(new("123", "workflow"), server));
        await client.GetAsync(AifaRequestContext.Endpoint);
        string firstId = server.Headers["x-request-id"];
        await client.GetAsync(AifaRequestContext.Endpoint);
        Assert.NotEqual(firstId, server.Headers["x-request-id"]);
        Assert.Equal("workflow", server.Headers["x-session-id"]);
        await client.GetAsync("https://api.openai.com/v1");
        Assert.Empty(server.Headers);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public async Task KeepsAgentEffortAndToolsWithAifaThinking(string effort)
    {
        ModelProviderDefinition provider = new(AifaRequestContext.Model, "AIFA", AifaRequestContext.Endpoint,
            true, false, 8192, 2048, true, effort, ["low", "medium", "high", "xhigh", "max"]);
        WorkflowSettings settings = new(provider.Url, provider.Id, "test-token", Provider: provider)
        {
            AifaContext = new("123", "session"),
        };
        RecordingHandler server = new();
        using IChatClient client = ModelChatClientFactory.Create(settings, server);
        ChatOptions options = AgentTeamFactory.CreateChatOptions(settings, "test instructions",
            [AIFunctionFactory.Create((string text) => text, name: "echo")]);
        options.Temperature = 0.25f;
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], options);

        using JsonDocument body = JsonDocument.Parse(server.Body!);
        Assert.True(body.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(effort, body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(0.25, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal("echo", body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task EnablesThinkingForStreamingRequests()
    {
        WorkflowSettings settings = new(AifaRequestContext.Endpoint, AifaRequestContext.Model, "test-token")
        {
            AifaContext = new("123", "session"),
        };
        RecordingHandler server = new();
        using IChatClient client = ModelChatClientFactory.Create(settings, server);
        StringBuilder text = new();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
            text.Append(update.Text);

        using JsonDocument body = JsonDocument.Parse(server.Body!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(body.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("low", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal("Hello", text.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("user\r\nx-injected: value")]
    public void RejectsInvalidIdentityHeaders(string user)
    {
        WorkflowSettings settings = new(AifaRequestContext.Endpoint, AifaRequestContext.Model, "token")
        {
            AifaContext = new(user, "session"),
        };
        Assert.Throws<ConfigurationException>(settings.Validate);
    }

    [Fact]
    public async Task PreservesIdentityHeadersAcrossRetries()
    {
        RetryHandler server = new();
        using HttpClient client = new(new AifaRequestHandler(new("123", "workflow"),
            new AiRequestRetryHandler(server, TimeSpan.FromMilliseconds(1))));
        using HttpResponseMessage response = await client.PostAsync(AifaRequestContext.Endpoint,
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, server.Ids.Count);
        Assert.Equal(server.Ids[0], server.Ids[1]);
    }

    [Theory]
    [InlineData("https://shared.example/v1", "https://shared.example/v1")]
    [InlineData(" https://shared.example/v1/chat/completions ", "https://shared.example/v1")]
    [InlineData("", null)]
    [InlineData("http://shared.example", null)]
    public void ReadsAndValidatesSharedEndpointFromAppsettings(string endpoint, string? expected)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { Providers = new { AIFA = new { Endpoint = endpoint } } }));
            if (expected is null)
                Assert.Throws<ConfigurationException>(() => AifaRequestContext.ReadEndpoint(path));
            else
                Assert.Equal(expected, AifaRequestContext.ReadEndpoint(path));
        }
        finally { File.Delete(path); }
    }

    private sealed class RetryHandler : HttpMessageHandler
    {
        public List<string> Ids = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Ids.Add(request.Headers.GetValues("x-request-id").Single());
            Assert.Equal("workflow", request.Headers.GetValues("x-session-id").Single());
            Assert.Equal("123", request.Headers.GetValues("x-user-id").Single());
            return Task.FromResult(new HttpResponseMessage(Ids.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Url;
        public string? Authorization;
        public string? ContentType;
        public string? Body;
        public Dictionary<string, string> Headers = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Headers = request.Headers.Where(header => header.Key.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(header => header.Key, header => header.Value.Single(), StringComparer.OrdinalIgnoreCase);
            if (Body is not null)
            {
                using JsonDocument body = JsonDocument.Parse(Body);
                if (body.RootElement.TryGetProperty("stream", out JsonElement stream) && stream.GetBoolean())
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "data: {\"id\":\"chatcmpl-aifa\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"developer-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hello\"},\"finish_reason\":null}]}\n\n" +
                            "data: {\"id\":\"chatcmpl-aifa\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"developer-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                            "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
                    };
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"chatcmpl-aifa","object":"chat.completion","created":1,"model":"developer-model",
                     "choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":null,
                       "tool_calls":[{"id":"call-1","type":"function","function":{"name":"search_repository","arguments":"{}"}}]}}],
                     "usage":{"prompt_tokens":10,"completion_tokens":3,"total_tokens":13}}
                    """, Encoding.UTF8, "application/json"),
            };
        }
    }
}
