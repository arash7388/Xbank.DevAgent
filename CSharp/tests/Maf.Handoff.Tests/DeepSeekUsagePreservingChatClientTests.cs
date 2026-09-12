using System.Runtime.CompilerServices;
using Maf.Handoff.Core;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class DeepSeekUsagePreservingChatClientTests
{
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
}
