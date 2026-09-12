using Maf.Handoff.Core;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class ModelChatClientFactoryTests
{
    [Fact]
    public void UsesNativeAdapterForDeepSeekThinkingHistory()
    {
        ModelProviderDefinition provider = new(
            "deepseek-v4-flash",
            "DeepSeek",
            "https://api.deepseek.com",
            ToolCalling: true,
            Vision: true,
            MaxInputTokens: 1_000_000,
            MaxOutputTokens: 384_000,
            Thinking: true,
            ReasoningEffort: "max",
            SupportsReasoningEffort: ["low", "medium", "high", "max"]);
        WorkflowSettings settings = new(
            provider.Url,
            provider.Id,
            "test-key",
            Provider: provider);

        IChatClient client = ModelChatClientFactory.Create(settings);

        Assert.Contains("DeepSeekToolHistoryChatClient", client.GetType().Name, StringComparison.Ordinal);
        Assert.IsType<DeepSeekUsagePreservingChatClient>(
            client.GetService(typeof(DeepSeekUsagePreservingChatClient)));
        (client as IDisposable)?.Dispose();
    }
}
