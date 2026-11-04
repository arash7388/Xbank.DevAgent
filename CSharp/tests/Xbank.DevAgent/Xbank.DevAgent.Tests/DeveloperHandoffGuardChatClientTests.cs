using System.Runtime.CompilerServices;
using Xbank.DevAgent.Core;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class DeveloperHandoffGuardChatClientTests
{
    [Fact]
    public async Task BlocksHandoffWithoutReplayingProviderHistoryAsync()
    {
        SequenceChatClient inner = new(
            HandoffResponse("handoff-1"),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "I will make the edit first.")));
        using DeveloperHandoffGuardChatClient guard = new(inner, () => false);

        ChatResponse response = await guard.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Change the setting.")]);

        Assert.Equal(1, inner.NonStreamingCallCount);
        Assert.Contains("handoff blocked", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllowsHandoffWhenDiffExistsAsync()
    {
        SequenceChatClient inner = new(HandoffResponse("handoff-1"));
        using DeveloperHandoffGuardChatClient guard = new(inner, () => true);

        ChatResponse response = await guard.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Review the completed edit.")]);

        Assert.Equal(1, inner.NonStreamingCallCount);
        Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
    }

    [Fact]
    public async Task BlocksStreamingHandoffWithoutReplayingProviderHistoryAsync()
    {
        SequenceChatClient inner = new(
            HandoffResponse("handoff-1"),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Editing now.")));
        using DeveloperHandoffGuardChatClient guard = new(inner, () => false);

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in guard.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Change the setting.")]))
        {
            updates.Add(update);
        }

        Assert.Equal(1, inner.StreamingCallCount);
        Assert.Contains("handoff blocked", updates.ToChatResponse().Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DoesNotLeakBlockedHandoffAsync()
    {
        SequenceChatClient inner = new(
            HandoffResponse("handoff-1"),
            HandoffResponse("handoff-2"),
            HandoffResponse("handoff-3"));
        using DeveloperHandoffGuardChatClient guard = new(inner, () => false);

        ChatResponse response = await guard.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Change the setting.")]);

        Assert.Equal(1, inner.NonStreamingCallCount);
        Assert.Empty(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Contains("handoff blocked", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static ChatResponse HandoffResponse(string callId) => new(new ChatMessage(
        ChatRole.Assistant,
        [new FunctionCallContent(callId, "handoff_to_1", new Dictionary<string, object?>())]));

    private sealed class SequenceChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public int NonStreamingCallCount { get; private set; }

        public int StreamingCallCount { get; private set; }

        public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            NonStreamingCallCount++;
            LastMessages = [.. messages];
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingCallCount++;
            LastMessages = [.. messages];
            await Task.Yield();
            foreach (ChatResponseUpdate update in _responses.Dequeue().ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
