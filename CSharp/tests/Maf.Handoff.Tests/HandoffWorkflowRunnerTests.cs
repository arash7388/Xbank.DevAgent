using System.Runtime.CompilerServices;
using Maf.Handoff.Core;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class HandoffWorkflowRunnerTests
{
    [Fact]
    public async Task TriggersTheStartingAgentTurn()
    {
        RecordingChatClient chatClient = new();
        WorkflowSettings settings = new(
            "https://example.test",
            "test-model",
            "test-key",
            MaxTotalMessages: 4,
            MaxAgentTurns: 1);
        HandoffWorkflowRunner runner = new(settings, chatClient: chatClient);

        List<RunEvent> events = [];
        await foreach (RunEvent runEvent in runner.RunAsync("Make a small configuration change."))
        {
            events.Add(runEvent);
        }

        Assert.True(chatClient.CallCount > 0);
        Assert.Contains(events, runEvent =>
            runEvent.Type == "message" && runEvent.Text?.Contains("Test response") is true);
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Test response")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Test response");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
