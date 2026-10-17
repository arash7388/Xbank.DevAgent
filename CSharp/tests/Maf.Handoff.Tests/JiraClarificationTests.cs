using System.Runtime.CompilerServices;
using System.Text.Json;
using Maf.Handoff.Core;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class JiraClarificationTests
{
    private const string Questions = "مقدار مورد انتظار RetryLimit چند است؟";

    [Theory]
    [InlineData("tool", false)]
    [InlineData("marker", false)]
    [InlineData("blocking_handoff", false)]
    [InlineData("tool", true)]
    [InlineData("marker", true)]
    [InlineData("blocking_handoff", true)]
    public async Task ClarificationUsesHumanAnswersOrDelegatedDecisionsAndCompletesSameWorkflow(string mode, bool autonomous)
    {
        InputJira jira = new();
        int intakeCalls = 0;
        int downstreamCalls = 0;
        string expectedAnswer = autonomous ? AutonomyPolicy.DelegatedDecision : "RetryLimit=7";
        string[] names = [AgentNames.Jira, .. AgentNames.Required];
        Dictionary<string, IChatClient> clients = names.ToDictionary(name => name, name => (IChatClient)new Client((messages, options) =>
        {
            if (name == AgentNames.Jira)
            {
                if (intakeCalls++ == 0)
                    return mode switch
                    {
                        "tool" => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("ask", "ask_jira_clarification",
                            new Dictionary<string, object?> { ["questionsInPersian"] = Questions })])),
                        "blocking_handoff" => ForwardHandoff(options!, "NEEDS_CLARIFICATION\n" + Questions),
                        _ => new(new ChatMessage(ChatRole.Assistant, Questions + "\nJIRA_INPUT_REQUIRED")),
                    };
                Assert.Contains(messages, message => message.Text.Contains(expectedAnswer, StringComparison.Ordinal) ||
                    message.Contents.OfType<FunctionResultContent>().Any(result => result.Result?.ToString()?.Contains(expectedAnswer) is true));
                return ForwardHandoff(options!, "READY_WITH_ASSUMPTIONS; no blocking questions remain");
            }
            downstreamCalls++;
            if (name == AgentNames.ReleaseManager)
                return new(new ChatMessage(ChatRole.Assistant, "WORKFLOW_COMPLETE"));
            return ForwardHandoff(options!, name == AgentNames.Reviewer ? "REVIEW_APPROVED" : "Confirmed RetryLimit=7");
        }));
        WorkflowSettings settings = new("https://example.test", "model", "key") { AutonomyLevel = autonomous ? 100 : 50 };
        WorkflowBundle bundle = HandoffWorkflowFactory.Create(clients, names.ToDictionary(name => name, _ => settings), jira: jira);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(bundle.Workflow, "Assess TASK-42", cancellationToken: timeout.Token);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        Task<ChatMessage[]> watch = CollectAsync(run, timeout.Token);
        if (!autonomous)
        {
            Assert.Contains(Questions, await jira.Asked.Task.WaitAsync(timeout.Token));
            Assert.Equal(0, downstreamCalls);
            jira.Answer.SetResult("RetryLimit=7");
        }
        ChatMessage[] transcript = await watch;
        Assert.Equal(!autonomous, jira.Asked.Task.IsCompleted);

        Assert.Equal(2, intakeCalls);
        Assert.Equal(4, downstreamCalls);
        Assert.Equal("completed", HandoffTopology.GetTerminationStatus(transcript, settings));
        Assert.Contains(transcript, message => message.Text.Contains(expectedAnswer, StringComparison.Ordinal) ||
            message.Contents.OfType<FunctionResultContent>().Any(result => result.Result?.ToString()?.Contains(expectedAnswer) is true));
        Assert.DoesNotContain(transcript, message => message.Role == ChatRole.Assistant && message.Text.Contains("JIRA_INPUT_REQUIRED"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NonBlockingAssumptionsPreserveHandoffWithoutAsking(bool streaming, bool markerInPayload)
    {
        InputJira jira = new();
        FunctionCallContent handoff = new("handoff", "handoff_to_planner",
            new Dictionary<string, object?>
            {
                ["reasonForHandoff"] = markerInPayload ? "READY_WITH_ASSUMPTIONS; proceed to Planner" : "Proceed to Planner",
            });
        Client client = new((_, _) => new(new ChatMessage(ChatRole.Assistant,
            [new TextContent(markerInPayload ? "Readiness confirmed" : "READY_WITH_ASSUMPTIONS; no blocking gaps"), handoff])));
        using JiraClarificationChatClient guard = new(client, jira);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        ChatResponse response;
        if (streaming)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in guard.GetStreamingResponseAsync([], cancellationToken: timeout.Token))
                updates.Add(update);
            response = updates.ToChatResponse();
        }
        else response = await guard.GetResponseAsync([], cancellationToken: timeout.Token);

        Assert.False(jira.Asked.Task.IsCompleted);
        Assert.Equal("handoff_to_planner", Assert.Single(response.Messages.SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()).Name);
    }

    [Fact]
    public async Task ClarificationRetriesRespectRequestLimit()
    {
        InputJira jira = new();
        jira.Answer.SetResult("The requirement is still unclear.");
        string[] names = [AgentNames.Jira, .. AgentNames.Required];
        int calls = 0;
        Dictionary<string, IChatClient> clients = names.ToDictionary(name => name, _ => (IChatClient)new Client((_, _) =>
        {
            calls++;
            return new(new ChatMessage(ChatRole.Assistant, Questions + "\nJIRA_INPUT_REQUIRED"));
        }));
        WorkflowSettings settings = new("https://example.test", "model", "key") { MaxLlmRequests = 2, MaxRepeatedWorkflowStates = 100 };
        WorkflowBundle bundle = HandoffWorkflowFactory.Create(clients, names.ToDictionary(name => name, _ => settings), jira: jira);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(bundle.Workflow, "Assess TASK-42", cancellationToken: timeout.Token);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        ChatMessage[] transcript = await CollectAsync(run, timeout.Token);
        Assert.Equal(2, calls);
        Assert.Equal("request_limit_reached", HandoffTopology.GetTerminationStatus(transcript, settings));
    }

    private static ChatResponse ForwardHandoff(ChatOptions options, string evidence)
    {
        AITool tool = options.Tools!.Last(item => item.Name.StartsWith("handoff_to_", StringComparison.Ordinal));
        string parameter = "reasonForHandoff";
        return new(new ChatMessage(ChatRole.Assistant,
            [new TextContent(evidence), new FunctionCallContent(Guid.NewGuid().ToString("N"), tool.Name, new Dictionary<string, object?> { [parameter] = evidence })]));
    }

    private static async Task<ChatMessage[]> CollectAsync(StreamingRun run, CancellationToken cancellationToken)
    {
        ChatMessage[] transcript = [];
        await foreach (WorkflowEvent item in run.WatchStreamAsync(cancellationToken))
        {
            Assert.IsNotType<WorkflowErrorEvent>(item);
            if (item is ExecutorFailedEvent failed) throw failed.Data ?? new InvalidOperationException("Executor failed without details.");
            if (item is WorkflowOutputEvent output) transcript = [.. output.As<IEnumerable<ChatMessage>>() ?? []];
        }
        return transcript;
    }

    private sealed class InputJira : IJiraIssueSearch, IJiraProblemReporter, IWorkflowUserInput
    {
        public TaskCompletionSource<string> Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> AskAsync(string questions, CancellationToken cancellationToken = default)
        {
            Asked.TrySetResult(questions);
            return Answer.Task.WaitAsync(cancellationToken);
        }
        public Task<JiraProblemReport> ReportProblemAsync(string problemInPersian, CancellationToken cancellationToken = default) => Task.FromResult(new JiraProblemReport("TASK-42", "posted", problemInPersian));
        public Task<JsonElement> GetIssueDetailsAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JsonElement> GetIssueCommentsAsync(string key, int startAt = 0, int maxResults = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraAttachmentInfo>> GetAttachmentsAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JiraAttachmentText> ReadAttachmentAsync(string key, string id, int offset = 0, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraIssueInfo>> SearchIssuesAsync(string jql, int maxResults = 10, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetIssueTitleByKeyAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Client(Func<ChatMessage[], ChatOptions?, ChatResponse> respond) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(respond([.. messages], options));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (ChatResponseUpdate update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
