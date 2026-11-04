using System.Runtime.CompilerServices;
using System.Text.Json;
using Xbank.DevAgent.Core;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class JiraProblemReportingTests
{
    private const string Problem = "معیار پذیرش مشخص نیست. لطفاً رفتار مورد انتظار را توضیح دهید.";

    [Fact]
    public async Task GatherOnlyIntakeReadsIssueAndHandsOffWithoutReportingOrClarification()
    {
        Reporter jira = new();
        int intakeCalls = 0, downstreamCalls = 0;
        string[] names = [AgentNames.Jira, .. AgentNames.Required];
        Dictionary<string, IChatClient> clients = names.ToDictionary(name => name, name => (IChatClient)new Client(options =>
        {
            if (name == AgentNames.Jira)
            {
                Assert.Contains("Jira validation is disabled", options!.Instructions);
                Assert.DoesNotContain(options.Tools!, tool => tool.Name is "report_jira_problem" or "ask_jira_clarification" or "ask_user_decision");
                Assert.Contains(options.Tools!, tool => tool.Name == "get_jira_issue_comments");
                Assert.Contains(options.Tools!, tool => tool.Name == "read_jira_attachment");
                if (intakeCalls++ == 0)
                    return new(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("details", "get_jira_issue_details", new Dictionary<string, object?> { ["issueKey"] = "WEPOD-42" })]));
            }
            else
            {
                downstreamCalls++;
                Assert.Contains(options!.Tools!, tool => tool.Name == "ask_user_decision");
                if (name == AgentNames.Planner)
                    Assert.Contains("it did not validate readiness or requirements", options.Instructions);
                if (name == AgentNames.ReleaseManager)
                    return new(new ChatMessage(ChatRole.Assistant, "WORKFLOW_COMPLETE"));
            }
            AITool handoff = options!.Tools!.Last(tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal));
            // Source material may quote a previous assessment; gathering must not trigger automatic reporting.
            string evidence = name == AgentNames.Jira ? "Source report: READY_WITH_ASSUMPTIONS\n" + Problem
                : name == AgentNames.Reviewer ? "REVIEW_APPROVED" : "Requirements validated; implement the plan.";
            return new(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(Guid.NewGuid().ToString("N"), handoff.Name, new Dictionary<string, object?> { ["reasonForHandoff"] = evidence })]));
        }));
        WorkflowSettings settings = new("https://example.test", "test-model", "test-key")
            { JiraValidationEnabled = false, AutonomyLevel = 50, ReleaseManagerValidationEnabled = true };
        WorkflowBundle bundle = HandoffWorkflowFactory.Create(clients, names.ToDictionary(name => name, _ => settings), jira: jira, userInput: new RejectInput());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(bundle.Workflow, "Gather WEPOD-42", cancellationToken: timeout.Token);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        ChatMessage[] transcript = [];
        await foreach (WorkflowEvent item in run.WatchStreamAsync(timeout.Token))
        {
            Assert.IsNotType<WorkflowErrorEvent>(item);
            Assert.IsNotType<ExecutorFailedEvent>(item);
            if (item is WorkflowOutputEvent output) transcript = [.. output.As<IEnumerable<ChatMessage>>() ?? []];
        }
        Assert.Equal("completed", HandoffTopology.GetTerminationStatus(transcript, settings));
        Assert.Equal(1, jira.DetailsReads);
        Assert.Equal(0, jira.Reports);
        Assert.Equal(2, intakeCalls);
        Assert.Equal(4, downstreamCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockingFinalAssessmentIsReportedWithoutAnotherModelCall(bool streaming)
    {
        Reporter jira = new();
        Client client = new(_ => new(new ChatMessage(ChatRole.Assistant,
            $"NEEDS_CLARIFICATION\n{Problem}\n<agent_summary>بررسی انجام شد.</agent_summary>\nJIRA_INPUT_REQUIRED")));
        using JiraProblemReportingChatClient guard = new(client, jira);

        ChatResponse response;
        if (streaming)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in guard.GetStreamingResponseAsync([])) updates.Add(update);
            response = updates.ToChatResponse();
        }
        else response = await guard.GetResponseAsync([]);

        Assert.Equal(1, client.Calls);
        Assert.Equal(1, jira.Reports);
        Assert.Contains(Problem, jira.Problem);
        Assert.DoesNotContain("agent_summary", jira.Problem);
        Assert.DoesNotContain("JIRA_INPUT_REQUIRED", jira.Problem);
        Assert.EndsWith("JIRA_INPUT_REQUIRED", response.Text);
    }

    [Fact]
    public async Task CleanReadinessAssessmentDoesNotPostAComment()
    {
        Reporter jira = new();
        using JiraProblemReportingChatClient guard = new(new Client(_ => new(new ChatMessage(ChatRole.Assistant, "READY"))), jira);
        await guard.GetResponseAsync([]);
        Assert.Equal(0, jira.Reports);
    }

    [Fact]
    public async Task NonBlockingProblemsAreReportedBeforeTheHandoffProceeds()
    {
        Reporter jira = new();
        ChatResponse handoff = new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("handoff", "handoff_to_planner", new Dictionary<string, object?>
            {
                ["reasonForHandoff"] = "READY_WITH_ASSUMPTIONS\n" + Problem,
            })]));
        using JiraProblemReportingChatClient guard = new(new Client(_ => handoff), jira);

        ChatResponse response = await guard.GetResponseAsync([]);

        Assert.Equal(1, jira.Reports);
        Assert.Contains(Problem, jira.Problem);
        Assert.Equal("handoff_to_planner", Assert.Single(response.Messages[0].Contents.OfType<FunctionCallContent>()).Name);
        Assert.False(CheckpointSerialization.SerializeResponse(response).Reusable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyJiraReceivesTheReportingToolAndBlockedIntakeStopsBeforePlanner(bool useTool)
    {
        Reporter jira = new();
        int jiraTurn = 0;
        Client intake = new(options =>
        {
            Assert.Contains(options!.Tools!, tool => tool.Name == AgentToolCatalog.ReportJiraProblem.Name);
            if (useTool && jiraTurn++ == 0)
                return new(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("report", AgentToolCatalog.ReportJiraProblem.Name,
                        new Dictionary<string, object?> { ["problemInPersian"] = Problem })]));
            return new(new ChatMessage(ChatRole.Assistant, Problem + "\nJIRA_INPUT_REQUIRED"));
        });
        Client other = new(options =>
        {
            Assert.DoesNotContain(options?.Tools ?? [], tool => tool.Name == AgentToolCatalog.ReportJiraProblem.Name);
            return new(new ChatMessage(ChatRole.Assistant, "No issue."));
        });
        string[] names = [.. AgentNames.Required, AgentNames.Jira];
        WorkflowSettings settings = new("https://example.test", "test-model", "test-key");
        Dictionary<string, IChatClient> clients = names.ToDictionary(name => name,
            name => (IChatClient)(name == AgentNames.Jira ? intake : other));
        Dictionary<string, WorkflowSettings> settingsByAgent = names.ToDictionary(name => name, _ => settings);
        AgentTeam team = AgentTeamFactory.Create(clients, settingsByAgent, jira: jira);
        await team.Planner!.RunAsync("Check available tools.");
        Assert.Equal(1, other.Calls);

        WorkflowBundle bundle = HandoffWorkflowFactory.Create(clients, settingsByAgent, jira: jira);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(bundle.Workflow, "Assess WEPOD-42", cancellationToken: timeout.Token);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        ChatMessage[] transcript = [];
        await foreach (WorkflowEvent item in run.WatchStreamAsync(timeout.Token))
        {
            Assert.IsNotType<WorkflowErrorEvent>(item);
            Assert.IsNotType<ExecutorFailedEvent>(item);
            if (item is WorkflowOutputEvent output) transcript = [.. output.As<IEnumerable<ChatMessage>>() ?? []];
        }
        Assert.Equal("input_required", HandoffTopology.GetTerminationStatus(transcript, settings));
        Assert.True(jira.Reports > 0);
        Assert.Equal(1, other.Calls); // Only the explicit tool inspection above ran Planner.
    }

    [Fact]
    public void ExternalCommentRequestsAndBlockingAssessmentsAreNotReusableCheckpoints()
    {
        ChatResponse request = new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("report", AgentToolCatalog.ReportJiraProblem.Name, new Dictionary<string, object?> { ["problemInPersian"] = Problem })]));
        Assert.False(CheckpointSerialization.SerializeResponse(request).Reusable);
        Assert.False(CheckpointSerialization.SerializeResponse(new(new ChatMessage(ChatRole.Assistant, Problem + "\nJIRA_INPUT_REQUIRED"))).Reusable);
        Assert.True(CheckpointSerialization.SerializeResponse(new(new ChatMessage(ChatRole.Assistant, "READY"))).Reusable);
    }

    private sealed class RejectInput : IWorkflowUserInput
    {
        public Task<string> AskAsync(string questions, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Gather-only intake must not ask for clarification.");
    }

    private sealed class Reporter : IJiraIssueSearch, IJiraProblemReporter
    {
        public int DetailsReads { get; private set; }
        public int Reports { get; private set; }
        public string Problem { get; private set; } = "";
        public Task<JiraProblemReport> ReportProblemAsync(string problemInPersian, CancellationToken cancellationToken = default)
        {
            Reports++;
            Problem = problemInPersian;
            return Task.FromResult(new JiraProblemReport("WEPOD-42", "posted", "نظر ثبت شد.", "321"));
        }
        public Task<JsonElement> GetIssueDetailsAsync(string key, CancellationToken cancellationToken = default)
        {
            DetailsReads++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { key, fields = new { summary = "Source task", description = Problem } }));
        }
        public Task<JsonElement> GetIssueCommentsAsync(string key, int startAt = 0, int maxResults = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraAttachmentInfo>> GetAttachmentsAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<JiraAttachmentText> ReadAttachmentAsync(string key, string id, int offset = 0, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JiraIssueInfo>> SearchIssuesAsync(string jql, int maxResults = 10, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetIssueTitleByKeyAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Client(Func<ChatOptions?, ChatResponse> respond) : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(respond(options));
        }
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
