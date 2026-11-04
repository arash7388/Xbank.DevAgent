using System.Runtime.CompilerServices;
using Xbank.DevAgent.Core;
using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Net;
using System.ClientModel.Primitives;
using DeepSeek;
using DeepSeek.Agents.AI;

namespace Xbank.DevAgent.Tests;

public sealed class DeepSeekUsagePreservingChatClientTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FinalResponseWithoutToolsStillProtectsEarlierToolCallHistory(bool streaming, bool emptyTools)
    {
        ValidatingReasoningHttpHandler handler = new();
        using HttpClient http = new(handler);
        using DeepSeekUsagePreservingChatClient client = new(CreateNativeClient(http));
        ChatOptions options = ToolOptions();
        // FunctionInvokingChatClient removes available tools when asking for its final response.
        options.Tools = emptyTools ? [] : null;
        ChatMessage[] history =
        [
            new(ChatRole.User, "Summarize the file evidence"),
            new(ChatRole.Assistant, [new FunctionCallContent("legacy-call", "read_file", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("legacy-call", "file evidence")]),
        ];

        await RespondAsync(client, history, options, streaming);

        using JsonDocument request = JsonDocument.Parse(Assert.Single(handler.Requests));
        Assert.Equal("disabled", request.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("legacy-call", request.RootElement.GetProperty("messages")[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Contains("file evidence", request.RootElement.GetProperty("messages")[2].GetProperty("content").GetString());
        Assert.NotNull(options.Reasoning);
    }

    [Fact]
    public async Task MissingOptionsStillProtectEarlierToolCallHistory()
    {
        ValidatingReasoningHttpHandler handler = new();
        using HttpClient http = new(handler);
        using DeepSeekUsagePreservingChatClient client = new(CreateNativeClient(http));

        await client.GetResponseAsync(
        [
            new(ChatRole.Assistant, [new FunctionCallContent("legacy-call", "read_file", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("legacy-call", "file evidence")]),
        ]);

        using JsonDocument request = JsonDocument.Parse(Assert.Single(handler.Requests));
        Assert.Equal("disabled", request.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task IncompleteHistoryDisablesThinkingWithoutLosingToolEvidence(
        bool streaming, bool toolTurn, bool explicitThinking)
    {
        ValidatingReasoningHttpHandler handler = new();
        using HttpClient http = new(handler);
        using DeepSeekUsagePreservingChatClient client = new(CreateNativeClient(http));
        ChatOptions options = ToolOptions();
        if (!explicitThinking) options.Reasoning = null; // DeepSeek defaults to thinking even without this option.
        options.AdditionalProperties = new() { ["user"] = "test-user" };
        if (explicitThinking) options.AdditionalProperties["thinking"] = true;
        ChatMessage assistant = toolTurn
            ? new(ChatRole.Assistant, [new FunctionCallContent("legacy-call", "read_file", new Dictionary<string, object?>())])
            : new(ChatRole.Assistant, "Earlier agent or workflow supervisor context");
        List<ChatMessage> history = [new(ChatRole.User, "Continue"), assistant];
        if (toolTurn) history.Add(new(ChatRole.Tool, [new FunctionResultContent("legacy-call", "file evidence") ]));

        await RespondAsync(client, history, options, streaming);

        using JsonDocument request = JsonDocument.Parse(Assert.Single(handler.Requests));
        Assert.Equal("disabled", request.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        JsonElement[] sent = request.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(history.Count, sent.Length);
        Assert.Equal("assistant", sent[1].GetProperty("role").GetString());
        if (toolTurn)
        {
            Assert.Equal("legacy-call", sent[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
            Assert.Contains("file evidence", sent[2].GetProperty("content").GetString());
        }
        if (explicitThinking) Assert.True((bool)options.AdditionalProperties["thinking"]!);
        else Assert.False(options.AdditionalProperties.ContainsKey("thinking"));
        Assert.Equal(explicitThinking, options.Reasoning is not null);
        Assert.Null(assistant.AdditionalProperties);
        Assert.DoesNotContain(assistant.Contents, content => content is TextReasoningContent);

        // The fallback applies to this request only, not to the client's subsequent clean conversations.
        await RespondAsync(client, [new(ChatRole.User, "New task")], ToolOptions(), streaming);
        using JsonDocument cleanRequest = JsonDocument.Parse(handler.Requests[1]);
        Assert.Equal("enabled", cleanRequest.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolCallCarriesReasoningAcrossClientsWhenMessageMetadataIsStripped(bool streaming)
    {
        ValidatingReasoningHttpHandler handler = new();
        using HttpClient http = new(handler);
        using DeepSeekUsagePreservingChatClient origin = new(CreateNativeClient(http));
        using DeepSeekUsagePreservingChatClient destination = new(CreateNativeClient(http));
        ChatMessage user = new(ChatRole.User, "Inspect the file");
        ChatResponse response = await RespondAsync(origin, [user], ToolOptions(), streaming);
        // Handoff history may keep function content while dropping message-level reasoning and IDs.
        ChatMessage handoff = new(ChatRole.Assistant, response.Messages[0].Contents.OfType<FunctionCallContent>().Cast<AIContent>().ToList());

        await RespondAsync(destination, [user, handoff,
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "file evidence")])], ToolOptions(), streaming);

        using JsonDocument request = JsonDocument.Parse(handler.Requests[1]);
        Assert.Equal("enabled", request.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("original reasoning", request.RootElement.GetProperty("messages")[1].GetProperty("reasoning_content").GetString());
        Assert.DoesNotContain(handoff.Contents, content => content is TextReasoningContent);
    }

    [Fact]
    public async Task RestoresJsonReasoningMetadataAndLearnsIncomingHandoffHistory()
    {
        ValidatingReasoningHttpHandler handler = new();
        using HttpClient http = new(handler);
        using DeepSeekUsagePreservingChatClient client = new(CreateNativeClient(http));
        using JsonDocument metadata = JsonDocument.Parse("\"persisted reasoning\"");
        ChatMessage assistant = new(ChatRole.Assistant,
            [new FunctionCallContent("persisted-call", "read_file", new Dictionary<string, object?>())])
        {
            AdditionalProperties = new() { ["reasoning_content"] = metadata.RootElement.Clone() },
        };
        ChatMessage result = new(ChatRole.Tool, [new FunctionResultContent("persisted-call", "file evidence")]);
        await client.GetResponseAsync([assistant, result], ToolOptions());
        ChatMessage stripped = new(ChatRole.Assistant,
            [new FunctionCallContent("persisted-call", "read_file", new Dictionary<string, object?>())]);
        await client.GetResponseAsync([stripped, result], ToolOptions());

        foreach (string body in handler.Requests)
        {
            using JsonDocument request = JsonDocument.Parse(body);
            Assert.Equal("enabled", request.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("persisted reasoning", request.RootElement.GetProperty("messages")[0].GetProperty("reasoning_content").GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplaysCompleteReasoningFromContentOrNativeRepresentation(bool rawRepresentation)
    {
        ValidatingReasoningHttpHandler handler = new();
        using HttpClient http = new(handler);
        using DeepSeekUsagePreservingChatClient client = new(CreateNativeClient(http));
        ChatMessage assistant = new(ChatRole.Assistant, "Plan");
        if (rawRepresentation)
        {
            assistant.RawRepresentation = new DeepSeek.Chat.ChatMessage { ReasoningContent = "complete reasoning" };
        }
        else
        {
            assistant.Contents.Add(new TextReasoningContent("complete "));
            assistant.Contents.Add(new TextReasoningContent("reasoning"));
            assistant.AdditionalProperties = new() { ["reasoning_content"] = "reasoning" };
        }

        await client.GetResponseAsync([assistant], ToolOptions());

        using JsonDocument request = JsonDocument.Parse(Assert.Single(handler.Requests));
        Assert.Equal("enabled", request.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("complete reasoning", request.RootElement.GetProperty("messages")[0].GetProperty("reasoning_content").GetString());
        if (!rawRepresentation) Assert.Equal("reasoning", assistant.AdditionalProperties!["reasoning_content"]);
    }

    [Fact]
    public async Task HistoryWithoutToolsKeepsConfiguredThinkingEvenWhenReasoningIsMissing()
    {
        ValidatingReasoningHttpHandler handler = new();
        using HttpClient http = new(handler);
        using DeepSeekUsagePreservingChatClient client = new(CreateNativeClient(http));
        ChatOptions options = ToolOptions();
        options.Tools = null;

        await client.GetResponseAsync([new(ChatRole.Assistant, "Previous response")], options);

        using JsonDocument request = JsonDocument.Parse(Assert.Single(handler.Requests));
        Assert.Equal("enabled", request.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    private static IChatClient CreateNativeClient(HttpClient http) =>
        new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new ClientRetryPolicy(0),
        }).GetChatClient("deepseek-flash").AsIChatClient();

    private static ChatOptions ToolOptions() => new()
    {
        Tools = [AIFunctionFactory.Create(() => "contents", "read_file")],
        Reasoning = new() { Effort = ReasoningEffort.High, Output = ReasoningOutput.Full },
    };

    private static async Task<ChatResponse> RespondAsync(IChatClient client, IEnumerable<ChatMessage> messages,
        ChatOptions options, bool streaming)
    {
        if (!streaming) return await client.GetResponseAsync(messages, options);
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, options)) updates.Add(update);
        return updates.ToChatResponse();
    }

    private sealed class ValidatingReasoningHttpHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add(body);
            using JsonDocument json = JsonDocument.Parse(body);
            bool thinkingDisabled = json.RootElement.TryGetProperty("thinking", out JsonElement thinking) &&
                thinking.GetProperty("type").GetString() == "disabled";
            bool hasToolHistory = json.RootElement.GetProperty("messages").EnumerateArray()
                .Any(message => message.TryGetProperty("tool_calls", out JsonElement calls) && calls.GetArrayLength() > 0);
            if (!thinkingDisabled && (json.RootElement.TryGetProperty("tools", out _) || hasToolHistory))
            {
                foreach (JsonElement message in json.RootElement.GetProperty("messages").EnumerateArray())
                {
                    if (message.GetProperty("role").GetString() == "assistant" &&
                        (!message.TryGetProperty("reasoning_content", out JsonElement reasoning) ||
                         string.IsNullOrWhiteSpace(reasoning.GetString())))
                    {
                        return new(HttpStatusCode.BadRequest)
                        {
                            Content = new StringContent("""
                                {"error":{"message":"The `reasoning_content` in the thinking mode must be passed back to the API.","type":"invalid_request_error"}}
                                """, System.Text.Encoding.UTF8, "application/json"),
                        };
                    }
                }
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"response","object":"chat.completion","created":1,"model":"deepseek-flash",
                     "choices":[{"index":0,"message":{"role":"assistant","content":null,"reasoning_content":"original reasoning",
                     "tool_calls":[{"id":"call-1","type":"function","function":{"name":"read_file","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}
                    """, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAdapterReplaysReasoningForTextAndToolTurns(bool streaming)
    {
        ReasoningHttpHandler handler = new();
        DeepSeekUsageCapture capture = new() { InnerHandler = handler };
        using HttpClient http = new(capture);
        IChatClient native = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            Transport = new HttpClientPipelineTransport(http),
        }).GetChatClient("deepseek-flash").AsIChatClient();
        using DeepSeekUsagePreservingChatClient client = new(native, capture);
        async Task<ChatResponse> Respond(IEnumerable<ChatMessage> messages)
        {
            if (!streaming) return await client.GetResponseAsync(messages);
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages))
                updates.Add(update);
            return updates.ToChatResponse();
        }
        ChatMessage user = new(ChatRole.User, "Inspect the file");
        ChatResponse first = await Respond([user]);
        // Framework history can retain text without retaining its reasoning content.
        ChatMessage stripped = new(ChatRole.Assistant, first.Text) { MessageId = first.Messages[0].MessageId };
        ChatResponse second = await Respond([user, stripped, new(ChatRole.User, "Read it")]);
        await Respond([user, stripped, .. second.Messages,
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "contents")])]);

        using JsonDocument request = JsonDocument.Parse(handler.LastRequest!);
        JsonElement[] assistants = request.RootElement.GetProperty("messages").EnumerateArray()
            .Where(message => message.GetProperty("role").GetString() == "assistant").ToArray();
        Assert.Equal(2, assistants.Length);
        Assert.Equal("text reasoning", assistants[0].GetProperty("reasoning_content").GetString());
        Assert.Equal("tool reasoning", assistants[1].GetProperty("reasoning_content").GetString());
    }

    private sealed class ReasoningHttpHandler : HttpMessageHandler
    {
        private int _calls;
        public string? LastRequest { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = await request.Content!.ReadAsStringAsync(cancellationToken);
            string message = _calls++ == 1
                ? """{"role":"assistant","content":null,"reasoning_content":"tool reasoning","tool_calls":[{"id":"call-1","type":"function","function":{"name":"read_file","arguments":"{}"}}]}"""
                : """{"role":"assistant","content":"Plan","reasoning_content":"text reasoning"}""";
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"response-" + _calls + "\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"deepseek-flash\",\"choices\":[{\"index\":0,\"message\":" + message + ",\"finish_reason\":\"stop\"}]}",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task NativeAdapterPreservesCacheBreakdownForCostCalculation()
    {
        DeepSeekUsageCapture capture = new() { InnerHandler = new UsageHttpHandler() };
        using HttpClient http = new(capture);
        IChatClient native = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            Transport = new HttpClientPipelineTransport(http),
        }).GetChatClient("deepseek-flash").AsIChatClient();
        using DeepSeekUsagePreservingChatClient client = new(native, capture);

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Hello")])) updates.Add(update);

        UsageDetails usage = Assert.IsType<UsageDetails>(updates.ToChatResponse().Usage);
        Assert.Equal(600_000, usage.InputTokenCount);
        Assert.Equal(550_000, usage.CachedInputTokenCount);
        Assert.Equal(11_680, usage.OutputTokenCount);
        Assert.Equal(5_000, usage.ReasoningTokenCount);
        Assert.Equal(611_680, usage.TotalTokenCount);

        ModelProviderDefinition provider = new("deepseek-flash", "DeepSeek", "https://api.deepseek.com",
            true, true, 1_000_000, 384_000, false, null, [],
            InputCostPerMillionTokens: 0.15m, OutputCostPerMillionTokens: 0.6m,
            CachedInputCostPerMillionTokens: 0.003m);
        TokenUsageCollector collector = new(new Dictionary<string, WorkflowSettings>
        {
            [AgentNames.Developer] = new("https://api.deepseek.com", "deepseek-flash", "test-key", Provider: provider),
        });
        collector.Record(AgentNames.Developer, usage);
        // A synthetic breakdown: reasoning is already included in output, and
        // cached input must not be billed a second time at the cache-miss rate.
        Assert.Equal(0.016158m, collector.Snapshot().EstimatedCostUsd);
    }

    [Fact]
    public void RawUsageCanRecoverInputFromCacheHitAndMissCounts()
    {
        using JsonDocument json = JsonDocument.Parse("""
            {"prompt_cache_hit_tokens": 100, "prompt_cache_miss_tokens": 20, "completion_tokens": 30}
            """);
        UsageDetails usage = DeepSeekUsageCapture.ParseUsage(json.RootElement);
        Assert.Equal(120, usage.InputTokenCount);
        Assert.Equal(100, usage.CachedInputTokenCount);
        Assert.Null(usage.ReasoningTokenCount);
    }

    private sealed class UsageHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"test","object":"chat.completion","created":1,"model":"deepseek-flash",
                     "choices":[{"index":0,"message":{"role":"assistant","content":"Done"},"finish_reason":"stop"}],
                     "usage":{"prompt_tokens":600000,"completion_tokens":11680,"total_tokens":611680,
                              "prompt_cache_hit_tokens":550000,"prompt_cache_miss_tokens":50000,
                              "completion_tokens_details":{"reasoning_tokens":5000}}}
                    """, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task StreamingPathPreservesNonStreamingUsage()
    {
        UsageDetails expected = new()
        {
            InputTokenCount = 120,
            CachedInputTokenCount = 20,
            OutputTokenCount = 30,
            ReasoningTokenCount = 10,
            TotalTokenCount = 150,
        };
        StubClient inner = new(expected);
        using DeepSeekUsagePreservingChatClient client = new(inner);

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Hello")]))
        {
            updates.Add(update);
        }

        ChatResponse reconstructed = updates.ToChatResponse();
        Assert.Equal(1, inner.NonStreamingCalls);
        Assert.Equal(0, inner.StreamingCalls);
        Assert.Equal(150, reconstructed.Usage?.TotalTokenCount);
        Assert.Equal(20, reconstructed.Usage?.CachedInputTokenCount);
    }

    [Fact]
    public async Task StreamingPathPreservesReasoningRequiredByToolCallHistory()
    {
        ChatResponse response = new(new ChatMessage(
            ChatRole.Assistant,
            [
                new TextReasoningContent("internal reasoning"),
                new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>()),
            ]));
        ResponseStubClient inner = new(response);
        using DeepSeekUsagePreservingChatClient client = new(inner);

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Inspect the file")]))
        {
            updates.Add(update);
        }

        ChatResponse reconstructed = updates.ToChatResponse();
        Assert.Contains(reconstructed.Messages.SelectMany(message => message.Contents),
            content => content is TextReasoningContent reasoning && reasoning.Text == "internal reasoning");
        Assert.Contains(reconstructed.Messages.SelectMany(message => message.Contents),
            content => content is FunctionCallContent call && call.CallId == "call-1");
    }

    [Fact]
    public async Task RestoresReasoningWhenFrameworkKeepsToolCallButDropsReasoningContent()
    {
        ChatResponse firstResponse = new(new ChatMessage(
            ChatRole.Assistant,
            [
                new TextReasoningContent("reasoning required by DeepSeek"),
                new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>()),
            ]));
        HistoryCapturingClient inner = new(firstResponse);
        using DeepSeekUsagePreservingChatClient client = new(inner);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Inspect the file")]);

        ChatMessage strippedAssistant = new(
            ChatRole.Assistant,
            [new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>())]);
        await client.GetResponseAsync(
        [
            strippedAssistant,
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "contents")]),
        ]);

        ChatMessage restored = Assert.Single(inner.LastMessages!, message => message.Role == ChatRole.Assistant);
        Assert.Contains(restored.Contents,
            content => content is TextReasoningContent reasoning && reasoning.Text == "reasoning required by DeepSeek");
        Assert.Equal("reasoning required by DeepSeek", restored.AdditionalProperties!["reasoning_content"]);
        Assert.DoesNotContain(strippedAssistant.Contents, content => content is TextReasoningContent);
    }

    private sealed class StubClient(UsageDetails usage) : IChatClient
    {
        public int NonStreamingCalls { get; private set; }
        public int StreamingCalls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            NonStreamingCalls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done"))
            {
                Usage = usage,
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingCalls++;
            await Task.Yield();
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ResponseStubClient(ChatResponse response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(response);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class HistoryCapturingClient(ChatResponse firstResponse) : IChatClient
    {
        private int _calls;

        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages = [.. messages];
            return Task.FromResult(_calls++ == 0
                ? firstResponse
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
