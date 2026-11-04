using System.Runtime.CompilerServices;
using Xbank.DevAgent.Core;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class ConversationTerminationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JiraClarificationStopsBeforePlanningOrImplementation(bool fragment)
    {
        ScriptedClient provider = new((_, _) => Text("NEEDS_CLARIFICATION: Which API consumers must be supported? " + HandoffTopology.JiraClarificationMarker), fragment);
        string[] names = [.. AgentNames.Required, AgentNames.Jira];
        WorkflowBundle bundle = HandoffWorkflowFactory.Create(
            names.ToDictionary(name => name, _ => (IChatClient)provider),
            names.ToDictionary(name => name, _ => Settings()));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
            bundle.Workflow, "Assess WEPOD-42", cancellationToken: timeout.Token);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        ChatMessage[]? transcript = null;
        await foreach (WorkflowEvent item in run.WatchStreamAsync(timeout.Token))
        {
            Assert.IsNotType<WorkflowErrorEvent>(item);
            Assert.IsNotType<ExecutorFailedEvent>(item);
            if (item is WorkflowOutputEvent output)
                transcript = [.. output.As<IEnumerable<ChatMessage>>() ?? []];
        }

        Assert.Equal(1, provider.CallCount);
        Assert.NotNull(transcript);
        Assert.Equal("input_required", HandoffTopology.GetTerminationStatus(transcript, Settings()));
        Assert.DoesNotContain(transcript, message => message.Role == ChatRole.Assistant && message.AuthorName != AgentNames.Jira);
    }

    [Fact]
    public async Task ReviewerApprovalInHandoffArgumentsCanComplete()
    {
        ScriptedClient provider = new((call, options) =>
        {
            if (call >= 4) return Text(HandoffTopology.CompletionMarker);
            ChatResponse response = Handoff(options);
            if (call == 3)
            {
                FunctionCallContent handoff = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
                handoff.Arguments!["reasonForHandoff"] = "REVIEW_APPROVED: independently inspected the latest diff.";
                foreach (ChatMessage message in response.Messages)
                    for (int index = message.Contents.Count - 1; index >= 0; index--)
                        if (message.Contents[index] is TextContent) message.Contents.RemoveAt(index);
                response.Messages.Last().Contents.Add(new TextContent(BuildFailureMessages.SkippedNotice));
            }
            return response;
        });

        List<RunEvent> events = await Run(provider);

        Assert.Equal(4, provider.CallCount);
        Assert.Equal("completed", Assert.Single(events, item => item.Type == "run_finished").Status);
    }

    [Fact]
    public async Task ReleaseCompletionWithoutReviewerConfirmationFails()
    {
        ScriptedClient provider = new((call, options) => call < 4
            ? Handoff(options, approveReview: false)
            : Text(HandoffTopology.CompletionMarker));

        List<RunEvent> events = await Run(provider);

        Assert.Equal(4, provider.CallCount);
        Assert.DoesNotContain(events, item => item.Type == "error");
        Assert.Equal("review_approval_missing", Assert.Single(events, item => item.Type == "run_finished").Status);
        Assert.DoesNotContain(events, item => item.Type == "decision" && item.Status == "completed");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReleaseDecisionIsReportedWithWholeOrFragmentedStreamingMarkers(bool reject, bool fragment)
    {
        ScriptedClient provider = new((call, options) => call < 4
            ? Handoff(options)
            : Text("Ready. " + (reject ? HandoffTopology.ReleaseRejectionMarker : HandoffTopology.CompletionMarker)), fragment);

        List<RunEvent> events = await Run(provider);

        Assert.Equal(4, provider.CallCount);
        Assert.DoesNotContain(events, item => item.Type == "error");
        Assert.Equal(reject ? "rejected_by_release" : "completed",
            Assert.Single(events, item => item.Type == "run_finished").Status);
        Assert.Equal(new[] { (AgentNames.Planner, AgentNames.Developer), (AgentNames.Developer, AgentNames.Reviewer), (AgentNames.Reviewer, AgentNames.ReleaseManager) },
            events.Where(item => item.Type == "handoff").Select(item => (item.Source!, item.Target!)));
        Assert.Single(events, item => item.Type == "decision");
        Assert.Equal(4, Assert.Single(events, item => item.Type == "usage").Usage!.LlmRequestCount);
    }

    [Fact]
    public async Task GlobalRequestBudgetStopsBeforeReleaseProviderCall()
    {
        ScriptedClient provider = new((_, options) => Handoff(options));

        List<RunEvent> events = await Run(provider, Settings() with { MaxLlmRequests = 3 });

        Assert.Equal(3, provider.CallCount);
        Assert.DoesNotContain(events, item => item.Type == "error");
        Assert.Equal("request_limit_reached", Assert.Single(events, item => item.Type == "run_finished").Status);
        Assert.Equal(3, Assert.Single(events, item => item.Type == "usage").Usage!.LlmRequestCount);
        Assert.DoesNotContain(events, item => item.Type == "decision" && item.Status == "completed");
    }

    [Fact]
    public async Task ApprovalOnTheLastAllowedRequestSucceeds()
    {
        ScriptedClient provider = new((call, options) => call < 4 ? Handoff(options) : Text(HandoffTopology.CompletionMarker));

        List<RunEvent> events = await Run(provider, Settings() with { MaxLlmRequests = 4, MaxAgentTurns = 1 });

        Assert.Equal(4, provider.CallCount);
        Assert.Equal("completed", Assert.Single(events, item => item.Type == "run_finished").Status);
    }

    [Fact]
    public async Task EarlyReleaseMarkerBeforeToolResultsCannotOverrideLaterRejection()
    {
        ScriptedClient provider = new((call, options) => call switch
        {
            1 or 2 or 3 => Handoff(options),
            4 => WithPrematureApproval(),
            _ => Text(HandoffTopology.ReleaseRejectionMarker),
        });

        List<RunEvent> events = await Run(provider, referenceDocuments: new SearchResult("Blocking finding."));

        Assert.Equal(5, provider.CallCount);
        Assert.Equal("rejected_by_release", Assert.Single(events, item => item.Type == "run_finished").Status);

        static ChatResponse WithPrematureApproval()
        {
            ChatResponse response = Tool(AgentToolCatalog.SearchDocuments.Name);
            response.Messages[0].Contents.Insert(0, new TextContent(HandoffTopology.CompletionMarker));
            return response;
        }
    }

    [Theory]
    [InlineData(HandoffTopology.RequestLimitMarker)]
    [InlineData(HandoffTopology.AgentTurnLimitMarker)]
    [InlineData(HandoffTopology.ReviewerRemediationLimitMarker)]
    public async Task UserMarkerWithNonHandoffResponseDoesNotStopTheRun(string marker)
    {
        ScriptedClient provider = new((call, options) => call switch
        {
            1 => Text("Inspecting requirements."),
            2 or 3 or 4 => Handoff(options),
            _ => Text(HandoffTopology.CompletionMarker),
        });
        HandoffWorkflowRunner runner = new(Settings(), chatClient: provider);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        List<RunEvent> events = [];
        await foreach (RunEvent item in runner.RunAsync($"Implement handling for {marker}.", timeout.Token)) events.Add(item);

        Assert.Equal(5, provider.CallCount);
        Assert.Equal("completed", Assert.Single(events, item => item.Type == "run_finished").Status);
    }

    [Fact]
    public async Task MessageBudgetStopsAnAgentThatNeverHandsOff()
    {
        ScriptedClient provider = new((_, _) => Text("Still inspecting."));

        List<RunEvent> events = await Run(provider, Settings() with { MaxTotalMessages = 4 });

        Assert.Equal("message_limit_reached", Assert.Single(events, item => item.Type == "run_finished").Status);
        Assert.DoesNotContain(events, item => item.Type == "handoff");
        Assert.DoesNotContain(events, item => item.Type == "decision" && item.Status == "completed");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AutonomousTurnLimitDoesNotApproveUnfinishedWork(int continuationLimit)
    {
        ScriptedClient provider = new((_, _) => Text("Still inspecting."));

        List<RunEvent> events = await Run(provider, Settings() with { MaxAgentTurns = continuationLimit });

        Assert.Equal(continuationLimit, provider.CallCount);
        Assert.Equal("agent_turn_limit_reached", Assert.Single(events, item => item.Type == "run_finished").Status);
        Assert.DoesNotContain(events, item => item.Type == "handoff");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ProviderFailureAtAnyRequiredStageReportsErrorWithoutApproval(int failingCall)
    {
        ScriptedClient provider = new((call, options) => call == failingCall
            ? throw new InvalidOperationException("simulated-provider-failure")
            : Handoff(options));

        List<RunEvent> events = await Run(provider);

        Assert.Equal(failingCall, provider.CallCount);
        Assert.Contains(events, item => item.Type == "error" &&
            item.Text?.Contains("simulated-provider-failure", StringComparison.Ordinal) is true);
        Assert.DoesNotContain(events, item => item.Type == "decision" && item.Status == "completed");
        Assert.DoesNotContain(events, item => item.Type == "run_finished" && item.Status == "completed");
        Assert.Single(events, item => item.Type == "usage");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task CancellationReachesTheActiveProviderAndDoesNotApprove(int cancellationCall)
    {
        using CancellationTokenSource cancellation = new();
        ScriptedClient provider = new((call, options) =>
        {
            if (call != cancellationCall) return Handoff(options);
            cancellation.Cancel();
            return Text("Unfinished.");
        }, waitForCancellationAtCall: cancellationCall);
        HandoffWorkflowRunner runner = new(Settings(), chatClient: provider);
        List<RunEvent> events = [];
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));

        bool canceled = false;
        try
        {
            await foreach (RunEvent item in runner.RunAsync("Implement the change.", cancellation.Token))
            {
                events.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        Assert.Equal(cancellationCall, provider.CallCount);
        Assert.True(provider.LastCancellationToken.IsCancellationRequested);
        Assert.True(canceled || events.Any(item => item.Type == "error"));
        Assert.DoesNotContain(events, item => item.Status == "completed");
    }

    [Fact]
    public async Task AlreadyCancelledRunMakesNoProviderCalls()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        ScriptedClient provider = new((_, options) => Handoff(options));
        HandoffWorkflowRunner runner = new(Settings(), chatClient: provider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (RunEvent _ in runner.RunAsync("Implement the change.", cancellation.Token)) { }
        });

        Assert.Equal(0, provider.CallCount);
    }

    [Theory]
    [InlineData(HandoffTopology.RequestLimitMarker)]
    [InlineData(HandoffTopology.ReviewerRemediationLimitMarker)]
    public async Task StopMarkersInToolResultsAreEvidenceAndDoNotStopTheRun(string marker)
    {
        ScriptedClient provider = new((call, options) => call switch
        {
            1 => Tool(AgentToolCatalog.SearchDocuments.Name),
            2 or 3 or 4 => Handoff(options),
            _ => Text(HandoffTopology.CompletionMarker),
        });

        List<RunEvent> events = await Run(provider, referenceDocuments: new SearchResult(marker));

        Assert.Equal(5, provider.CallCount);
        Assert.Equal("completed", Assert.Single(events, item => item.Type == "run_finished").Status);
    }

    [Fact]
    public async Task FailedReadToolReturnsErrorEvidenceForTheAgentToRecover()
    {
        ScriptedClient provider = new((call, options) => call switch
        {
            1 => Tool(AgentToolCatalog.SearchDocuments.Name),
            2 or 3 or 4 => Handoff(options),
            _ => Text(HandoffTopology.CompletionMarker),
        });

        List<RunEvent> events = await Run(provider, referenceDocuments: new SearchResult(null));

        Assert.Equal(5, provider.CallCount);
        Assert.Contains(provider.Inputs[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
            result => result.Exception is not null || result.Result?.ToString()?.Contains("Error", StringComparison.OrdinalIgnoreCase) is true);
        Assert.Equal("completed", Assert.Single(events, item => item.Type == "run_finished").Status);
    }

    private static WorkflowSettings Settings() => new("https://example.test", "scripted-model", "test-key",
        MaxTotalMessages: 40, MaxAgentTurns: 10);

    private static async Task<List<RunEvent>> Run(ScriptedClient provider, WorkflowSettings? settings = null,
        IReferenceDocumentSearch? referenceDocuments = null)
    {
        HandoffWorkflowRunner runner = new(settings ?? Settings(), chatClient: provider,
            referenceDocuments: referenceDocuments);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        List<RunEvent> events = [];
        await foreach (RunEvent item in runner.RunAsync("Implement the change.", timeout.Token)) events.Add(item);
        return events;
    }

    private static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));
    private static ChatResponse Handoff(ChatOptions? options, bool approveReview = true)
    {
        ChatResponse response = Tool(options!.Tools!.Last(tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal)).Name);
        if (approveReview && options.Instructions?.Contains("You are an independent senior code reviewer.", StringComparison.Ordinal) is true)
            response.Messages[0].Contents.Insert(0, new TextContent(HandoffTopology.ReviewApprovalMarker));
        return response;
    }
    private static ChatResponse Tool(string name) => new(new ChatMessage(ChatRole.Assistant,
        [new FunctionCallContent(Guid.NewGuid().ToString("N"), name,
            name == AgentToolCatalog.SearchDocuments.Name
                ? new Dictionary<string, object?> { ["query"] = "requirements", ["maxResults"] = 3 }
                : new Dictionary<string, object?> { ["reasonForHandoff"] = "Evidence reviewed." })]));

    private sealed class SearchResult(string? result) : IReferenceDocumentSearch
    {
        public Task<string> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            result is null ? throw new InvalidOperationException("simulated-read-failure") : Task.FromResult(result);
    }

    private sealed class ScriptedClient(Func<int, ChatOptions?, ChatResponse> next, bool fragment = false,
        int? waitForCancellationAtCall = null) : IChatClient
    {
        public int CallCount { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }
        public List<ChatMessage[]> Inputs { get; } = [];
        private ChatResponse Response(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            Inputs.Add([.. messages]);
            return next(++CallCount, options);
        }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(Response(messages, options));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastCancellationToken = cancellationToken;
            await Task.Yield();
            ChatResponse response = Response(messages, options);
            if (CallCount == waitForCancellationAtCall) await Task.Delay(Timeout.Infinite, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
            {
                if (fragment && update.Text.Length > 1 && !update.Contents.OfType<FunctionCallContent>().Any())
                {
                    foreach (char character in update.Text)
                    {
                        yield return new ChatResponseUpdate(ChatRole.Assistant, character.ToString());
                    }
                }
                else yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}
