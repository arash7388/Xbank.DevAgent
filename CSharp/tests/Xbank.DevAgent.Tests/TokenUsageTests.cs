using Xbank.DevAgent.Core;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace Xbank.DevAgent.Tests;

public sealed class TokenUsageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AifaCostsZeroAndMixedRunsOnlyChargePaidProviders(bool hasUsage)
    {
        ModelProviderDefinition paid = new("model", "Paid", "https://example.test",
            true, false, 1000, 1000, false, null, [], 2m, 8m, 0.5m);
        WorkflowSettings aifa = new(AifaRequestContext.Endpoint, AifaRequestContext.Model, "key",
            Provider: paid with { Url = AifaRequestContext.Endpoint });
        TokenUsageCollector collector = new(new Dictionary<string, WorkflowSettings>
        {
            [AgentNames.Planner] = aifa,
            [AgentNames.Developer] = new("https://example.test", "model", "key", Provider: paid),
        }, maxBudgetUsd: 0.01m);
        Assert.True(collector.RecordRequest(AgentNames.Planner));
        if (hasUsage) collector.Record(AgentNames.Planner, new UsageDetails
        {
            InputTokenCount = 1_000_000, OutputTokenCount = 500_000, TotalTokenCount = 1_500_000,
        });
        Assert.Equal(0m, collector.Snapshot().EstimatedCostUsd);
        Assert.True(collector.RecordRequest(AgentNames.Developer));
        collector.Record(AgentNames.Developer, new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 500 });
        TokenUsageSummary summary = collector.Snapshot();
        Assert.Equal(0m, summary.ByAgent.Single(item => item.Agent == AgentNames.Planner).EstimatedCostUsd);
        Assert.Equal(0.006m, summary.EstimatedCostUsd);
        Assert.Equal(hasUsage ? 1_501_500 : 1500, summary.TotalTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BudgetStopsFurtherCallsAcrossAgents(bool streaming)
    {
        ModelProviderDefinition provider = new(
            "test-model", "Test provider", "https://example.test",
            ToolCalling: true, Vision: false, MaxInputTokens: 1_000, MaxOutputTokens: 1_000,
            Thinking: false, ReasoningEffort: null, SupportsReasoningEffort: [],
            InputCostPerMillionTokens: 2m, OutputCostPerMillionTokens: 8m);
        WorkflowSettings settings = new("https://example.test", "test-model", "key", Provider: provider);
        TokenUsageCollector collector = new(new Dictionary<string, WorkflowSettings>
        {
            [AgentNames.Planner] = settings,
            [AgentNames.Developer] = settings,
        }, maxLlmRequests: 30, maxBudgetUsd: 0.10m);
        Assert.True(collector.RecordRequest(AgentNames.Planner));
        collector.Record(AgentNames.Planner, new UsageDetails
        {
            InputTokenCount = 50_000,
            OutputTokenCount = 0,
        });
        UsageResponseChatClient inner = new();
        IChatClient client = new UsageTrackingChatClient(inner, AgentNames.Developer, collector);
        ChatMessage[] messages = [new(ChatRole.User, "Next call")];
        string text;
        if (streaming)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages))
                updates.Add(update);
            text = updates.ToChatResponse().Text;
        }
        else text = (await client.GetResponseAsync(messages)).Text;

        Assert.Equal(0, inner.CallCount);
        Assert.Equal(1, collector.Snapshot().LlmRequestCount);
        Assert.Contains(HandoffTopology.BudgetLimitMarker, text);
        Assert.False(CheckpointSerialization.SerializeResponse(new(new ChatMessage(ChatRole.Assistant, text))).Reusable);
        Assert.Equal("budget_limit_reached", HandoffTopology.GetTerminationStatus(
            [new ChatMessage(ChatRole.Assistant, text)], settings));
    }

    [Fact]
    public void AggregatesProviderReportedUsagePerAgentAndCalculatesCost()
    {
        ModelProviderDefinition provider = new(
            "test-model",
            "Test provider",
            "https://example.test",
            ToolCalling: true,
            Vision: false,
            MaxInputTokens: 1_000,
            MaxOutputTokens: 1_000,
            Thinking: false,
            ReasoningEffort: null,
            SupportsReasoningEffort: [],
            InputCostPerMillionTokens: 2m,
            OutputCostPerMillionTokens: 8m,
            CachedInputCostPerMillionTokens: 0.5m);
        WorkflowSettings settings = new("https://example.test", "test-model", "key", Provider: provider);
        TokenUsageCollector collector = new(new Dictionary<string, WorkflowSettings>
        {
            [AgentNames.Planner] = settings,
            [AgentNames.Developer] = settings,
        });

        collector.RecordRequest(AgentNames.Planner);
        collector.Record(AgentNames.Planner, new UsageDetails
        {
            InputTokenCount = 1_200,
            CachedInputTokenCount = 200,
            OutputTokenCount = 300,
            ReasoningTokenCount = 100,
            TotalTokenCount = 1_500,
        });
        collector.RecordRequest(AgentNames.Developer);
        collector.Record(AgentNames.Developer, new UsageDetails
        {
            InputTokenCount = 800,
            OutputTokenCount = 700,
            TotalTokenCount = 1_500,
        });

        TokenUsageSummary summary = collector.Snapshot();

        Assert.Equal(2, summary.LlmRequestCount);
        Assert.Equal(2_000, summary.InputTokens);
        Assert.Equal(1_000, summary.OutputTokens);
        Assert.Equal(3_000, summary.TotalTokens);
        Assert.Equal(200, summary.CachedInputTokens);
        Assert.Equal(100, summary.ReasoningTokens);
        Assert.Equal(0.0117m, summary.EstimatedCostUsd);
        Assert.Collection(summary.ByAgent,
            planner => Assert.Equal(AgentNames.Planner, planner.Agent),
            developer => Assert.Equal(AgentNames.Developer, developer.Agent));
    }

    [Fact]
    public void OmitsCostInsteadOfGuessingWhenPricingIsNotConfigured()
    {
        WorkflowSettings settings = new("https://example.test", "test-model", "key");
        TokenUsageCollector collector = new(new Dictionary<string, WorkflowSettings>
        {
            [AgentNames.Developer] = settings,
        });

        collector.RecordRequest(AgentNames.Developer);
        collector.Record(AgentNames.Developer, new UsageDetails
        {
            InputTokenCount = 10,
            OutputTokenCount = 5,
            TotalTokenCount = 15,
        });

        TokenUsageSummary summary = collector.Snapshot();

        Assert.True(summary.HasUsage);
        Assert.Null(summary.EstimatedCostUsd);
        Assert.Null(summary.ByAgent[0].EstimatedCostUsd);
    }

    [Fact]
    public void CountsRequestsWhenTheProviderOmitsUsage()
    {
        WorkflowSettings settings = new("https://example.test", "test-model", "key");
        TokenUsageCollector collector = new(new Dictionary<string, WorkflowSettings>
        {
            [AgentNames.Developer] = settings,
        });

        collector.RecordRequest(AgentNames.Developer);

        TokenUsageSummary summary = collector.Snapshot();

        Assert.True(summary.HasRequests);
        Assert.False(summary.HasUsage);
        Assert.Equal(1, summary.LlmRequestCount);
        Assert.Equal(1, Assert.Single(summary.ByAgent).LlmRequestCount);
    }

    [Fact]
    public void StopsBeforeSendingMoreThanTheRequestBudget()
    {
        WorkflowSettings settings = new("https://example.test", "test-model", "key");
        TokenUsageCollector collector = new(new Dictionary<string, WorkflowSettings>
        {
            [AgentNames.Developer] = settings,
        }, maxLlmRequests: 2);

        Assert.True(collector.RecordRequest(AgentNames.Developer));
        Assert.True(collector.RecordRequest(AgentNames.Developer));

        bool reserved = collector.RecordRequest(AgentNames.Developer);

        Assert.False(reserved);
        Assert.Contains("2 AI requests", collector.RequestLimitMessage(AgentNames.Developer), StringComparison.Ordinal);
        Assert.Equal(2, collector.Snapshot().LlmRequestCount);
    }

    [Fact]
    public async Task CapturesUsageReturnedByChatClient()
    {
        WorkflowSettings settings = new("https://example.test", "test-model", "key");
        TokenUsageCollector collector = new(new Dictionary<string, WorkflowSettings>
        {
            [AgentNames.Developer] = settings,
        });
        IChatClient client = new UsageTrackingChatClient(
            new UsageResponseChatClient(),
            AgentNames.Developer,
            collector);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]);

        AgentTokenUsage usage = Assert.Single(collector.Snapshot().ByAgent);
        Assert.Equal(1, usage.LlmRequestCount);
        Assert.Equal(42, usage.InputTokens);
        Assert.Equal(17, usage.OutputTokens);
        Assert.Equal(59, usage.TotalTokens);
    }

    [Fact]
    public async Task ReturnsAWorkflowMarkerInsteadOfThrowingAtTheRequestLimit()
    {
        WorkflowSettings settings = new("https://example.test", "test-model", "key");
        TokenUsageCollector collector = new(new Dictionary<string, WorkflowSettings>
        {
            [AgentNames.Developer] = settings,
        }, maxLlmRequests: 1);
        UsageResponseChatClient provider = new();
        IChatClient client = new UsageTrackingChatClient(provider, AgentNames.Developer, collector);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "First call")]);
        ChatResponse limited = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Second call")]);

        Assert.Equal(1, provider.CallCount);
        Assert.True(HandoffTopology.IsRequestLimitMessage(limited.Text));
        Assert.Equal(1, collector.Snapshot().LlmRequestCount);
    }

    [Fact]
    public void RequestLimitMarkerIsNotAReusableCheckpoint()
    {
        ChatResponse response = new(new ChatMessage(
            ChatRole.Assistant,
            $"{HandoffTopology.RequestLimitMarker} Stop."));

        Assert.False(CheckpointSerialization.SerializeResponse(response).Reusable);
    }

    private sealed class UsageResponseChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done"))
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = 42,
                    OutputTokenCount = 17,
                    TotalTokenCount = 59,
                },
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Done");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
