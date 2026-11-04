using System.Reflection;
using System.Text.Json;
using Xbank.DevAgent.Api;
using Xbank.DevAgent.Core;
using Microsoft.Extensions.Configuration;

namespace Xbank.DevAgent.Tests;

public sealed class SessionWorkflowRetryTests
{
    private static readonly Type StateType = typeof(RepositorySessionService).GetNestedType("SessionState", BindingFlags.NonPublic)!;
    private static CreateSessionRequest Request(bool enabled = true, int retries = 2) => new(
        "invalid-repository-url", "main", "TASK-42 implement feature",
        new Dictionary<string, AgentModelConfiguration>(), null,
        RetryOnWorkflowAgent: enabled, NumberOfRetries: retries);

    private static object State(CreateSessionRequest request, Action<RunEvent>? save = null) =>
        Activator.CreateInstance(StateType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [Guid.NewGuid(), request, save ?? (_ => { })], null)!;

    private static object? Invoke(object state, string method, params object?[] args) =>
        StateType.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, args);
    private static SessionSnapshot Snapshot(object state) => (SessionSnapshot)Invoke(state, "Snapshot")!;
    private static CancellationTokenSource Cancellation(object state) =>
        (CancellationTokenSource)StateType.GetProperty("Cancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
    private static bool Retry(object state, int retry = 1) => (bool)Invoke(state, "TryBeginRetry", "provider stream interrupted", retry)!;
    private static void Field(object state, string name, object? value) =>
        StateType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(state, value);

    [Theory]
    [InlineData(false, 2, 0)]
    [InlineData(true, 1, 1)]
    [InlineData(true, 2, 2)]
    public async Task ExecutionRetriesOnlyTheConfiguredNumberAndPersistsFinalFailure(bool enabled, int retries, int expected)
    {
        MemoryStore store = new();
        object state = State(Request(enabled, retries), store.Events.Add);
        using var cancellation = Cancellation(state);
        RepositorySessionService service = Service(store);
        await (Task)typeof(RepositorySessionService).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [state, null])!;

        Assert.Equal(expected, store.Events.Count(item => item.Type == "workflow_retry"));
        Assert.Equal("failed", Snapshot(state).Status);
        Assert.False(Snapshot(state).ExecutionPending);
        Assert.Contains("HTTPS", Snapshot(state).Error);
        Assert.Equal("failed", store.LastSnapshot!.Status);
    }

    [Fact]
    public async Task CancellationDuringRetryDelayStopsFurtherAttempts()
    {
        MemoryStore store = new();
        object? state = null;
        state = State(Request(), item => { store.Events.Add(item); if (item.Type == "workflow_retry") Invoke(state!, "Cancel"); });
        using var cancellation = Cancellation(state);
        await (Task)typeof(RepositorySessionService).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Service(store), [state, null])!;
        Assert.Equal("cancelled", Snapshot(state).Status);
        Assert.Single(store.Events, item => item.Type == "workflow_retry");
    }

    [Fact]
    public void RetryClearsPartialResultAndCarriesErrorAndOriginalTaskIntoFreshConversation()
    {
        object state = State(Request());
        using var cancellation = Cancellation(state);
        var originalWorkspace = Workspace("previous-attempt");
        Invoke(state, "BeginAgentRun", originalWorkspace);
        Invoke(state, "MarkFailed", "stream interrupted", "partial diff");
        Invoke(state, "AddEvent", RunEvent.AgentSummary(AgentNames.Developer, "stale summary"));
        Invoke(state, "AddEvent", new RunEvent("build_result", Text: "stale build"));
        Assert.True(Retry(state));
        Assert.Equal("cloning", Snapshot(state).Status);
        Assert.Null(Snapshot(state).Error);
        Assert.Empty(Snapshot(state).Diff);
        Assert.Null(StateType.GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state));
        Assert.DoesNotContain(Snapshot(state).Events, item => item.Type is "agent_summary" or "build_result");
        string task = (string)Invoke(state, "WorkflowTask")!;
        Assert.Contains(Request().Task, task);
        Assert.Contains("provider stream interrupted", task);
        Assert.Contains("cloned fresh", task);
        string? firstScope = (string?)StateType.GetProperty("RetryAttemptId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state);
        Invoke(state, "BeginAgentRun", Workspace("new-attempt"));
        Assert.Equal("running", Snapshot(state).Status);
        Assert.True(Retry(state, 2));
        Assert.NotEqual(firstScope, StateType.GetProperty("RetryAttemptId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state));
        Assert.False(Retry(state, 3));
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("awaiting_input")]
    [InlineData("awaiting_approval")]
    [InlineData("approving")]
    [InlineData("committed")]
    [InlineData("pushed")]
    public void RetryDoesNotRestartStoppedOrPublishingRuns(string status)
    {
        object state = State(Request());
        using var cancellation = Cancellation(state);
        Field(state, "_status", status);
        Assert.False(Retry(state));
        Assert.Equal(status, Snapshot(state).Status);
    }

    [Fact]
    public void RetryKeepsCumulativeUsageAndHonorsCallAndCostLimits()
    {
        object state = State(Request() with { MaxAiCalls = 5, MaxBudgetUsd = 1m });
        using var cancellation = Cancellation(state);
        Invoke(state, "AddEvent", RunEvent.TokenUsage(Usage(2, 0.2m)));
        Assert.True(Retry(state));
        Invoke(state, "AddEvent", RunEvent.TokenUsage(Usage(1, 0.1m)));
        Invoke(state, "AddEvent", RunEvent.TokenUsage(Usage(2, 0.2m)));
        Assert.Equal(4, Snapshot(state).Usage!.LlmRequestCount);
        Assert.Equal(0.4m, Snapshot(state).Usage!.EstimatedCostUsd);
        Assert.True(Retry(state, 2));
        Invoke(state, "AddEvent", RunEvent.TokenUsage(Usage(1, 0.1m)));
        Assert.Equal(5, Snapshot(state).Usage!.LlmRequestCount);
        Assert.False(Retry(state));
        Field(state, "_usage", Usage(1, 1m));
        Assert.False(Retry(state));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(11)]
    public void InvalidRetryCountsAreRejected(int count)
    {
        var error = Assert.Throws<TargetInvocationException>(() => typeof(RepositorySessionService)
            .GetMethod("Validate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Service(new()), [Request(retries: count)]));
        Assert.IsType<ArgumentException>(error.InnerException);
        Assert.Contains("retries", error.InnerException.Message);
    }

    [Fact]
    public void RetrySettingsAreOptInAndPersisted()
    {
        Assert.False(new CreateSessionRequest("repo", "main", "task", new Dictionary<string, AgentModelConfiguration>(), null).RetryOnWorkflowAgent);
        string json = (string)typeof(RepositorySessionService).GetMethod("SerializeWorkflowConfig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Service(new()), [Request(retries: 3)])!;
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("RetryOnWorkflowAgent").GetBoolean());
        Assert.Equal(3, document.RootElement.GetProperty("NumberOfRetries").GetInt32());
    }

    private static TokenUsageSummary Usage(int calls, decimal cost) => new([], calls, 0, 0, 0, 0, 0, cost);
    private static RepositorySessionService Service(MemoryStore store) => new(null!, null!, null!, null!, null!, null!, null!, store,
        new ConfigurationBuilder().Build());
    private static RepositoryWorkspace Workspace(string name) => (RepositoryWorkspace)Activator.CreateInstance(typeof(RepositoryWorkspace),
        BindingFlags.Instance | BindingFlags.NonPublic, null, [Path.Combine(Path.GetTempPath(), name), "repo", "main", null], null)!;

    private sealed class MemoryStore : IWorkflowRunStore
    {
        internal List<RunEvent> Events { get; } = [];
        internal SessionSnapshot? LastSnapshot { get; private set; }
        public void Create(Guid runId, string workflowType, string configJson, DateTimeOffset createdAt) { }
        public void UpdateStatus(Guid runId, string status, string? error = null) { }
        public void SaveSnapshot(Guid runId, string snapshotJson) => LastSnapshot = JsonSerializer.Deserialize<SessionSnapshot>(snapshotJson);
        public void AppendEvent(Guid runId, RunEvent runEvent) => Events.Add(runEvent);
        public WorkflowRunLog GetLog(Guid runId) => throw new NotSupportedException();
        public string? GetSnapshot(Guid runId) => null;
        public IReadOnlyList<WorkflowRunLogEntry> ListRuns(int limit = 100, int offset = 0) => [];
    }
}
