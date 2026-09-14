using System.Runtime.CompilerServices;
using Maf.Handoff.Core;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class ExclusiveHandoffChatClientTests
{
    [Fact]
    public async Task KeepsOrdinaryToolAndDefersHandoffAsync()
    {
        SequenceChatClient inner = new(MixedResponse());
        using ExclusiveHandoffChatClient guard = new(inner);

        ChatResponse response = await guard.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Inspect and then hand off.")]);

        FunctionCallContent call = Assert.Single(
            response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal("read_repository_file", call.Name);

        ChatResponse deferred = await guard.GetResponseAsync(
            [new ChatMessage(ChatRole.Tool, "File contents")]);

        FunctionCallContent handoff = Assert.Single(
            deferred.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal("handoff_to_1", handoff.Name);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task KeepsHandoffWhenItIsTheOnlyToolAsync()
    {
        SequenceChatClient inner = new(HandoffResponse());
        using ExclusiveHandoffChatClient guard = new(inner);

        ChatResponse response = await guard.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hand off now.")]);

        FunctionCallContent call = Assert.Single(
            response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.StartsWith("handoff_to_", call.Name, StringComparison.Ordinal);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task StreamingKeepsOrdinaryToolAndDefersHandoffAsync()
    {
        SequenceChatClient inner = new(MixedResponse());
        using ExclusiveHandoffChatClient guard = new(inner);

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in guard.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Inspect and then hand off.")]))
        {
            updates.Add(update);
        }

        FunctionCallContent call = Assert.Single(
            updates.ToChatResponse().Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>());
        Assert.Equal("read_repository_file", call.Name);

        List<ChatResponseUpdate> deferred = [];
        await foreach (ChatResponseUpdate update in guard.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.Tool, "File contents")]))
        {
            deferred.Add(update);
        }

        FunctionCallContent handoff = Assert.Single(
            deferred.ToChatResponse().Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>());
        Assert.Equal("handoff_to_1", handoff.Name);
        Assert.Equal(1, inner.CallCount);
    }

    private static ChatResponse MixedResponse() => new(new ChatMessage(
        ChatRole.Assistant,
        [
            new FunctionCallContent("tool-1", "read_repository_file", new Dictionary<string, object?>()),
            new FunctionCallContent("handoff-1", "handoff_to_1", new Dictionary<string, object?>()),
        ]));

    private static ChatResponse HandoffResponse() => new(new ChatMessage(
        ChatRole.Assistant,
        [new FunctionCallContent("handoff-1", "handoff_to_1", new Dictionary<string, object?>())]));

    private sealed class SequenceChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
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
