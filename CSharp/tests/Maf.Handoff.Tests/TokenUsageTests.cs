using Maf.Handoff.Core;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace Maf.Handoff.Tests;

public sealed class TokenUsageTests
{
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
            [AgentNames.Intake] = settings,
            [AgentNames.Developer] = settings,
        });

        collector.Record(AgentNames.Intake, new UsageDetails
        {
            InputTokenCount = 1_200,
            CachedInputTokenCount = 200,
            OutputTokenCount = 300,
            ReasoningTokenCount = 100,
            TotalTokenCount = 1_500,
        });
        collector.Record(AgentNames.Developer, new UsageDetails
        {
            InputTokenCount = 800,
            OutputTokenCount = 700,
            TotalTokenCount = 1_500,
        });

        TokenUsageSummary summary = collector.Snapshot();

        Assert.Equal(2_000, summary.InputTokens);
        Assert.Equal(1_000, summary.OutputTokens);
        Assert.Equal(3_000, summary.TotalTokens);
        Assert.Equal(200, summary.CachedInputTokens);
        Assert.Equal(100, summary.ReasoningTokens);
        Assert.Equal(0.0117m, summary.EstimatedCostUsd);
        Assert.Collection(summary.ByAgent,
            intake => Assert.Equal(AgentNames.Intake, intake.Agent),
            developer => Assert.Equal(AgentNames.Developer, developer.Agent));
    }

    [Fact]
    public void OmitsCostInsteadOfGuessingWhenPricingIsNotConfigured()
    {
        WorkflowSettings settings = new("https://example.test", "test-model", "key");
        TokenUsageCollector collector = new(new Dictionary<string, WorkflowSettings>
        {
            [AgentNames.Intake] = settings,
        });

        collector.Record(AgentNames.Intake, new UsageDetails
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
        Assert.Equal(42, usage.InputTokens);
        Assert.Equal(17, usage.OutputTokens);
        Assert.Equal(59, usage.TotalTokens);
    }

    private sealed class UsageResponseChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done"))
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = 42,
                    OutputTokenCount = 17,
                    TotalTokenCount = 59,
                },
            });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Done");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
