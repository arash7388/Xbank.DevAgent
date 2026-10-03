using System.Runtime.CompilerServices;
using Maf.Handoff.Core;
using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Net;
using System.ClientModel.Primitives;
using DeepSeek;
using DeepSeek.Agents.AI;

namespace Maf.Handoff.Tests;

public sealed class DeepSeekUsagePreservingChatClientTests
{
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
