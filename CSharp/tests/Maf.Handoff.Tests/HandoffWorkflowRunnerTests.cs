using System.Runtime.CompilerServices;
using Maf.Handoff.Core;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class HandoffWorkflowRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmitsSummariesAndOptionalBuildNotificationWithoutExtraModelCalls(bool skipBuild)
    {
        SummaryHandoffChatClient chatClient = new();
        WorkflowSettings settings = new("https://example.test", "test-model", "test-key",
            MaxTotalMessages: 30, MaxAgentTurns: 5) { SkipBuildVerification = skipBuild };
        HandoffWorkflowRunner runner = new(settings, chatClient: chatClient);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        List<RunEvent> events = [];

        await foreach (RunEvent item in runner.RunAsync("Make a small change.", timeout.Token)) events.Add(item);

        Assert.DoesNotContain(events, item => item.Type == "error");
        Assert.Contains(events, item => item.Type == "run_finished" && item.Status == "completed");
        Assert.Equal(4, chatClient.CallCount);
        if (skipBuild)
        {
            RunEvent warning = Assert.Single(events, item => item.Type == "build_warning");
            Assert.Equal("skipped", warning.Status);
            Assert.Equal(BuildFailureMessages.SkippedNotice, warning.Text);
        }
        else Assert.DoesNotContain(events, item => item.Type == "build_warning");
        foreach (string name in AgentNames.Required)
        {
            RunEvent summary = events.Last(item => item.Type == "agent_summary" && item.Agent == name);
            Assert.Equal("provided", summary.Status);
            Assert.Equal($"{name} completed its own work.", summary.Text);
        }
        Assert.DoesNotContain(events, item => item.Type == "agent_summary" && item.Agent is "jira" or "security");
    }

    [Fact]
    public async Task RetainsSeparateSummaryStatesWhenProviderFailsBeforeResponding()
    {
        RecordingChatClient chatClient = new(fail: true);
        WorkflowSettings settings = new("https://example.test", "test-model", "test-key");
        HandoffWorkflowRunner runner = new(settings, chatClient: chatClient);
        List<RunEvent> events = [];

        await foreach (RunEvent item in runner.RunAsync("Make a small change.")) events.Add(item);

        Assert.Contains(events, item => item.Type == "error");
        Assert.Equal(1, chatClient.CallCount);
        Assert.Equal("unavailable", events.Last(item => item.Type == "agent_summary" && item.Agent == "planner").Status);
        Assert.Equal("not_run", events.Last(item => item.Type == "agent_summary" && item.Agent == "developer").Status);
    }

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
        Assert.Contains(events, runEvent =>
            runEvent.Type == "trace" && runEvent.Category == "response" &&
            runEvent.Text == "Response completed. Reply: Test response");
        Assert.Equal("unavailable", events.Last(item => item.Type == "agent_summary" && item.Agent == "planner").Status);
        Assert.Equal("not_run", events.Last(item => item.Type == "agent_summary" && item.Agent == "developer").Status);
    }

    [Fact]
    public async Task ExecutesOrdinaryToolBeforeHandoffWhenProviderRequestsBoth()
    {
        MixedToolAndHandoffChatClient chatClient = new();
        RecordingReferenceDocumentSearch referenceDocuments = new();
        WorkflowSettings settings = new(
            "https://example.test",
            "test-model",
            "test-key",
            MaxTotalMessages: 8,
            MaxAgentTurns: 2);
        HandoffWorkflowRunner runner = new(
            settings,
            chatClient: chatClient,
            referenceDocuments: referenceDocuments);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        List<RunEvent> events = [];
        await foreach (RunEvent runEvent in runner.RunAsync(
            "Inspect the repository and implement the change.",
            timeout.Token))
        {
            events.Add(runEvent);
        }

        Assert.DoesNotContain(events, runEvent => runEvent.Type == "error");
        Assert.Equal(1, referenceDocuments.CallCount);
        Assert.Contains(events, runEvent => runEvent.Type == "handoff");
    }

    private sealed class SummaryHandoffChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(Next(options));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (ChatResponseUpdate update in Next(options).ToChatResponseUpdates()) yield return update;
        }

        private ChatResponse Next(ChatOptions? options)
        {
            int index = CallCount++;
            string agent = AgentNames.Required[index];
            string text = $"<agent_summary>{agent} completed its own work.</agent_summary>";
            if (agent == AgentNames.ReleaseManager)
                return new(new ChatMessage(ChatRole.Assistant, text + "\nWORKFLOW_COMPLETE"));
            if (agent == AgentNames.Reviewer) text += "\nREVIEW_APPROVED";
            string handoff = options!.Tools!.Last(tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal)).Name;
            return new(new ChatMessage(ChatRole.Assistant,
                [new TextContent(text), new FunctionCallContent($"handoff-{index}", handoff,
                    new Dictionary<string, object?> { ["reasonForHandoff"] = text })]));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }

    private sealed class RecordingChatClient(bool fail = false) : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (fail) throw new InvalidOperationException("Test provider failure.");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Test response")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (fail) throw new InvalidOperationException("Test provider failure.");
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Test response");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class MixedToolAndHandoffChatClient : IChatClient
    {
        private bool _mixedResponseReturned;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(NextResponse(options));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (ChatResponseUpdate update in NextResponse(options).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }

        private ChatResponse NextResponse(ChatOptions? options)
        {
            string? handoff = options?.Tools?.Select(tool => tool.Name)
                .FirstOrDefault(name => name.StartsWith("handoff_to_", StringComparison.Ordinal));
            if (handoff is null)
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Ready. WORKFLOW_COMPLETE"));
            }

            if (!_mixedResponseReturned)
            {
                _mixedResponseReturned = true;
                return new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent("tool-developer", AgentToolCatalog.SearchDocuments.Name, new Dictionary<string, object?>
                        {
                            ["query"] = "requirements",
                            ["maxResults"] = 3,
                        }),
                        new FunctionCallContent("handoff-developer", handoff, new Dictionary<string, object?>()),
                    ]));
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("handoff-developer-retry", handoff, new Dictionary<string, object?>())]));
        }
    }

    private sealed class RecordingReferenceDocumentSearch : IReferenceDocumentSearch
    {
        public int CallCount { get; private set; }

        public Task<string> SearchAsync(
            string query,
            int maxResults,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult("No matching reference text.");
        }
    }
}
