using System.Runtime.CompilerServices;
using System.Text.Json;
using Maf.Handoff.Core;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class JiraProblemReportingTests
{
    private const string Problem = "معیار پذیرش مشخص نیست. لطفاً رفتار مورد انتظار را توضیح دهید.";

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
        await team.Planner.RunAsync("Check available tools.");
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

    private sealed class Reporter : IJiraIssueSearch, IJiraProblemReporter
    {
        public int Reports { get; private set; }
        public string Problem { get; private set; } = "";
        public Task<JiraProblemReport> ReportProblemAsync(string problemInPersian, CancellationToken cancellationToken = default)
        {
            Reports++;
            Problem = problemInPersian;
            return Task.FromResult(new JiraProblemReport("WEPOD-42", "posted", "نظر ثبت شد.", "321"));
        }
        public Task<JsonElement> GetIssueDetailsAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
