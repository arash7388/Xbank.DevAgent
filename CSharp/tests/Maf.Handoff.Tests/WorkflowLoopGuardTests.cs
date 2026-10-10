using System.Runtime.CompilerServices;
using Maf.Handoff.Core;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class WorkflowLoopGuardTests
{
    [Fact]
    public async Task WorkflowRunnerReportsLoopInsteadOfSpendingTheRemainingRequestBudget()
    {
        HandoffLoopClient provider = new();
        WorkflowSettings settings = Settings with { MaxLlmRequests = 60, MaxReviewerRemediationCycles = 20 };
        HandoffWorkflowRunner runner = new(settings, chatClient: provider);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        List<RunEvent> events = [];
        await foreach (RunEvent item in runner.RunAsync("Implement and independently review this change", timeout.Token))
            events.Add(item);

        Assert.DoesNotContain(events, item => item.Type == "error");
        Assert.Contains(events, item => item.Type == "run_finished" && item.Status == "loop_detected");
        Assert.Contains(events, item => item.Text?.Contains(HandoffTopology.LoopRecoveryMarker) is true);
        Assert.True(provider.Calls < 20, $"Unexpected provider calls: {provider.Calls}");
        Assert.DoesNotContain(events, item => item.Type == "run_finished" && item.Status == "completed");
    }
    [Fact]
    public void RepeatedHandoffsGetOneRecoveryPerAgentThenLatchStop()
    {
        WorkflowLoopBudget budget = new(3);
        for (int cycle = 0; cycle < 2; cycle++)
        {
            Assert.Null(budget.Observe(AgentNames.Developer, [], "diff").Recovery);
            Assert.Null(budget.Observe(AgentNames.Reviewer, [], "diff").Recovery);
        }
        Assert.Contains("recovery attempt", budget.Observe(AgentNames.Developer, [], "diff").Recovery);
        Assert.NotNull(budget.Observe(AgentNames.Reviewer, [], "diff").Recovery);
        string stop = budget.Observe(AgentNames.Developer, [], "diff").Stop!;
        Assert.Contains(HandoffTopology.LoopDetectedMarker, stop);
        Assert.Contains("developer -> reviewer", stop);
        Assert.Equal(stop, budget.Observe(AgentNames.ReleaseManager, [], "new-diff").Stop);
    }

    [Fact]
    public void NewRepositoryContentAndToolEvidenceAllowRealProgress()
    {
        WorkflowLoopBudget budget = new(2);
        Assert.Null(budget.Observe("developer", [], "v1").Recovery);
        Assert.Null(budget.Observe("developer", [], "v2").Recovery);
        Assert.Null(budget.Observe("developer", ToolHistory("read", "new evidence"), "v2").Recovery);
        Assert.Null(budget.Observe("developer", ToolHistory("read-again", "more evidence"), "v2").Recovery);
    }

    [Fact]
    public void NewCallIdsAndParaphrasedHandoffsDoNotCountAsProgress()
    {
        WorkflowLoopBudget budget = new(2);
        Assert.Null(budget.Observe("reviewer", ToolHistory("call-1", "same output"), "v1").Recovery);
        ChatMessage[] history = [.. ToolHistory("call-2", "same output"),
            new(ChatRole.Assistant, "Paraphrased claims of progress"),
            new(ChatRole.Assistant, [new FunctionCallContent("handoff-1", "handoff_to_1",
                new Dictionary<string, object?> { ["reasonForHandoff"] = "Different words" })]),
            new(ChatRole.Tool, [new FunctionResultContent("handoff-1", "Changed handoff payload")])];
        Assert.NotNull(budget.Observe("reviewer", history, "v1").Recovery);
        Assert.NotNull(budget.Observe("reviewer", ToolHistory("call-3", "same output"), "v1").Stop);
    }

    [Fact]
    public void CyclingBetweenEarlierRepositoryStatesIsDetected()
    {
        WorkflowLoopBudget budget = new(2);
        budget.Observe("developer", [], "v1");
        budget.Observe("developer", [], "v2");
        Assert.NotNull(budget.Observe("developer", [], "v1").Recovery);
        Assert.NotNull(budget.Observe("developer", [], "v2").Stop);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardStopsProviderCallsAndRefreshesRoleWithoutMutatingHistory(bool streaming)
    {
        RecordingClient provider = new();
        WorkflowLoopBudget budget = new(2);
        WorkflowLoopGuardChatClient guard = new(provider, AgentNames.Reviewer, budget, () => "unchanged");
        ChatOptions options = new() { Instructions = "Original role instructions" };
        ChatMessage[] history = [new(ChatRole.Assistant, "I am the implementer") { AuthorName = "developer" }];
        ChatResponse first = await Invoke();
        ChatResponse recovery = await Invoke();
        Assert.Contains(HandoffTopology.LoopRecoveryMarker, string.Join("", recovery.Messages.Select(message => message.Text)));
        Assert.Contains("ACTIVE WORKFLOW ROLE: reviewer", provider.LastOptions!.Instructions);
        Assert.Contains("not the implementer", provider.LastOptions.Instructions);
        Assert.Contains("WORKFLOW SUPERVISOR", provider.LastOptions.Instructions);
        Assert.Equal("Original role instructions", options.Instructions);
        Assert.Equal("I am the implementer", history[0].Text);
        ChatResponse stop = await Invoke();
        Assert.Equal(2, provider.Calls);
        Assert.Equal("loop_detected", HandoffTopology.GetTerminationStatus(stop.Messages.ToArray(), Settings));
        Assert.DoesNotContain(stop.Messages.SelectMany(message => message.Contents), content => content is FunctionCallContent);

        async Task<ChatResponse> Invoke()
        {
            if (!streaming) return await guard.GetResponseAsync(history, options);
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in guard.GetStreamingResponseAsync(history, options)) updates.Add(update);
            return updates.ToChatResponse();
        }
    }

    [Fact]
    public async Task ExistingRequestCapStillBoundsLoopsThatKeepProducingDifferentEvidence()
    {
        RecordingClient provider = new();
        WorkflowSettings settings = Settings with { MaxLlmRequests = 3 };
        IChatClient guard = new WorkflowLoopGuardChatClient(
            new WorkflowExecutionGuardChatClient(provider, "reviewer", new(settings)),
            "reviewer", new(3), () => "v1");
        ChatResponse? last = null;
        for (int index = 0; index < 4; index++)
            last = await guard.GetResponseAsync(ToolHistory($"call-{index}", $"different evidence {index}"));
        Assert.Equal(3, provider.Calls);
        Assert.Contains(HandoffTopology.RequestLimitMarker, last!.Messages[0].Text);
    }

    private static WorkflowSettings Settings => new("https://example.test", "model", "key");

    private static ChatMessage[] ToolHistory(string id, string result) =>
    [new(ChatRole.Assistant, [new FunctionCallContent(id, "read_repository_file", new Dictionary<string, object?>())]),
        new(ChatRole.Tool, [new FunctionResultContent(id, result)])];

    private sealed class RecordingClient : IChatClient
    {
        public int Calls { get; private set; }
        public ChatOptions? LastOptions { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Still reviewing") { AuthorName = "reviewer" }));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class HandoffLoopClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            string handoff = options!.Tools!.First(tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal)).Name;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(Guid.NewGuid().ToString(), handoff,
                    new Dictionary<string, object?> { ["reasonForHandoff"] = "Please review the unchanged implementation" })])));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
