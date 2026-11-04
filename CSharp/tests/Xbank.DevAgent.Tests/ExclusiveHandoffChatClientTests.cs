using System.Runtime.CompilerServices;
using Xbank.DevAgent.Core;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class ExclusiveHandoffChatClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanAbandonStaleHandoffAfterToolFailure(bool streaming)
    {
        SequenceChatClient inner = new(MixedResponse(), new ChatResponse(new ChatMessage(
            ChatRole.Assistant, "The edit failed; I must fix it before review.")));
        using ExclusiveHandoffChatClient guard = new(inner);
        await guard.GetResponseAsync([new ChatMessage(ChatRole.User, "Implement the change.")]);
        ChatMessage result = new(ChatRole.Tool, [new FunctionResultContent("tool-1", "Tool failed")]);

        ChatResponse response;
        if (streaming)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in guard.GetStreamingResponseAsync([result]))
            {
                updates.Add(update);
            }
            response = updates.ToChatResponse();
        }
        else
        {
            response = await guard.GetResponseAsync([result]);
        }

        Assert.Empty(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Contains("edit failed", response.Text);
        Assert.Equal(2, inner.CallCount);
        Assert.Contains(inner.LastMessages.SelectMany(message => message.Contents),
            content => content is FunctionResultContent { CallId: "tool-1" });
    }

    [Fact]
    public async Task KeepsOrdinaryToolAndReconsidersHandoffAfterResultsAsync()
    {
        SequenceChatClient inner = new(MixedResponse(), HandoffResponse());
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
        Assert.Equal("handoff-2", handoff.CallId);
        Assert.Equal(2, inner.CallCount);
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
    public async Task StreamingKeepsOrdinaryToolAndReconsidersHandoffAfterResultsAsync()
    {
        SequenceChatClient inner = new(MixedResponse(), HandoffResponse());
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
        Assert.Equal("handoff-2", handoff.CallId);
        Assert.Equal(2, inner.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlocksAmbiguousMultipleHandoffsAndPreservesReasoning(bool streaming)
    {
        SequenceChatClient inner = new(new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new TextReasoningContent("provider reasoning"),
            new FunctionCallContent("return", "handoff_to_developer", new Dictionary<string, object?>()),
            new FunctionCallContent("approve", "handoff_to_security", new Dictionary<string, object?>()),
        ])));
        using ExclusiveHandoffChatClient guard = new(inner);
        ChatResponse response;
        if (streaming)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in guard.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "Review the patch.")])) updates.Add(update);
            response = updates.ToChatResponse();
        }
        else response = await guard.GetResponseAsync([new ChatMessage(ChatRole.User, "Review the patch.")]);

        Assert.Empty(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Contains("exactly one", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<TextReasoningContent>());
        Assert.Equal(1, inner.CallCount);
    }

    private static ChatResponse MixedResponse() => new(new ChatMessage(
        ChatRole.Assistant,
        [
            new TextReasoningContent("reasoning required by DeepSeek"),
            new FunctionCallContent("tool-1", "read_repository_file", new Dictionary<string, object?>()),
            new FunctionCallContent("handoff-1", "handoff_to_1", new Dictionary<string, object?>()),
        ]));

    private static ChatResponse HandoffResponse() => new(new ChatMessage(
        ChatRole.Assistant,
        [new FunctionCallContent("handoff-2", "handoff_to_1", new Dictionary<string, object?>())]));

    private sealed class SequenceChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public int CallCount { get; private set; }
        public ChatMessage[] LastMessages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastMessages = [.. messages];
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
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
