using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Maf.Handoff.Api;
using Maf.Handoff.Core;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class SessionModificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maf-modification-" + Guid.NewGuid().ToString("N"));
    private readonly Type _type = typeof(RepositorySessionService).GetNestedType("SessionState", BindingFlags.NonPublic)!;
    private readonly object _state;
    private readonly RepositoryWorkspace _workspace;
    private readonly List<RunEvent> _savedEvents = [];

    public SessionModificationTests()
    {
        Directory.CreateDirectory(_root);
        Git("init", "--initial-branch=main");
        File.WriteAllText(Path.Combine(_root, "settings.txt"), "original\n");
        Git("add", ".");
        Git("commit", "-m", "initial");
        _workspace = (RepositoryWorkspace)Activator.CreateInstance(typeof(RepositoryWorkspace),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [_root, "https://github.com/example/repo", "main", null], null)!;
        CreateSessionRequest request = new(_workspace.RepositoryUrl, "main", "WEPOD-42 update settings",
            new Dictionary<string, AgentModelConfiguration>(), null, AutonomyLevel: 50);
        _state = Activator.CreateInstance(_type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [Guid.NewGuid(), request, (Action<RunEvent>)(_savedEvents.Add)], null)!;
        Invoke("BeginAgentRun", _workspace);
        _workspace.WriteFile("settings.txt", "first result\n");
        Finish();
    }

    [Fact]
    public async Task RevisionsPreservePatchAndContextThenRepeatIndependentReviewBeforeApproval()
    {
        SessionSnapshot original = Snapshot();
        CancellationTokenSource oldCancellation = Cancellation;
        Add(new("usage", Usage: Usage(2)));
        Add(RunEvent.AgentSummary(AgentNames.Reviewer, "Previous result reviewed."));
        Add(new("build_result", Text: "Previous build passed.", Status: "passed"));
        Assert.Same(_workspace, Invoke("BeginModification", new ModifySessionRequest("  Also include a fallback  ")));
        Assert.NotSame(oldCancellation, Cancellation);
        Assert.Equal(original.Id, Snapshot().Id);
        Assert.Equal(original.Task, Snapshot().Task);
        Assert.Equal(original.Diff, Snapshot().Diff);
        Assert.Equal("first result\n", _workspace.ReadFile("settings.txt"));
        Assert.False(Snapshot().CanRequestChanges);
        Assert.True(Snapshot().ExecutionPending);
        Assert.Null(Snapshot().Error);
        Assert.DoesNotContain(Snapshot().Events, item => item.Type is "agent_summary" or "build_result");
        AssertError<InvalidOperationException>("BeginApproval");
        AssertError<InvalidOperationException>("BeginModification", new ModifySessionRequest("Concurrent request"));

        List<string> activated = [];
        RevisionClient client = new(_workspace, activated);
        WorkflowSettings settings = new("https://example.test", "test-model", "test-key") { SkipBuildVerification = true };
        HandoffWorkflowRunner runner = new(settings, _workspace, client);
        string task = (string)Invoke("WorkflowTask")!;
        Assert.Contains(original.Task, task);
        Assert.Contains("Also include a fallback", task);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        await foreach (RunEvent item in runner.RunAsync(task, timeout.Token)) Add(item);
        Assert.Equal(AgentNames.Required, activated);
        Assert.Contains(Snapshot().Events, item => item.Type == "run_finished" && item.Status == "completed");
        Finish();
        SessionSnapshot revised = Snapshot();
        Assert.Equal("awaiting_approval", revised.Status);
        Assert.True(revised.CanRequestChanges);
        Assert.Contains("+first result", revised.Diff);
        Assert.Contains("+fallback", revised.Diff);
        Assert.Null(revised.Approval);
        Assert.Equal("main", Git("branch", "--show-current"));

        Invoke("BeginModification", new ModifySessionRequest("Use a different fallback"));
        task = (string)Invoke("WorkflowTask")!;
        Assert.Contains("Modification 1:\nAlso include a fallback", task);
        Assert.Contains("Modification 2:\nUse a different fallback", task);
        Assert.Equal(2, Snapshot().ModificationPrompts!.Count);
        Assert.Equal(2, _savedEvents.Count(item => item.Type == "modification_requested"));
        Add(new("usage", Usage: Usage(3)));
        Add(new("usage", Usage: Usage(4)));
        // Streaming usage snapshots replace the current revision's counts; prior usage is added once.
        Assert.Equal(revised.Usage!.LlmRequestCount + 4, Snapshot().Usage!.LlmRequestCount);
        Assert.Equal(revised.Usage.TotalTokens + 40, Snapshot().Usage!.TotalTokens);
        Finish();
        Assert.Same(_workspace, Invoke("BeginApproval"));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("awaiting_approval")]
    [InlineData("no_changes")]
    public void FinishedUncommittedResultsCanBeRevisedWithFreshTimeoutAndManualCompletion(string status)
    {
        Set("_status", status);
        Set("_error", "Previous notice");
        CreateSessionRequest request = (CreateSessionRequest)_type.GetProperty("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_state)!;
        _type.GetProperty("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_state,
            request with { AutoCommit = true, AutoPush = true, AutoCreatePullRequest = true });
        Cancellation.Cancel();
        Invoke("BeginModification", new ModifySessionRequest("Revise it"));
        Assert.False(Cancellation.IsCancellationRequested);
        Assert.Null(Snapshot().Error);
        Finish();
        Assert.Equal("awaiting_approval", Snapshot().Status);
        Assert.False(Snapshot().AutomaticCompletionPending);
        request = (CreateSessionRequest)_type.GetProperty("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_state)!;
        Assert.False(request.AutoCommit || request.AutoPush || request.AutoCreatePullRequest);
    }

    [Theory]
    [InlineData("cloning")]
    [InlineData("running")]
    [InlineData("awaiting_input")]
    [InlineData("approving")]
    [InlineData("committed")]
    [InlineData("pushed")]
    [InlineData("creating_pull_request")]
    [InlineData("pull_request_created")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    public void OtherStatesCannotBeModified(string status)
    {
        Set("_status", status);
        Assert.False(Snapshot().CanRequestChanges);
        AssertError<InvalidOperationException>("BeginModification", new ModifySessionRequest("Change it"));
        Assert.Empty(Snapshot().ModificationPrompts!);
        Assert.Equal(status, Snapshot().Status);
    }

    [Theory]
    [InlineData("_executionPending")]
    [InlineData("_automaticCompletionPending")]
    public void FinishingOrPublishingRunsCannotBeModified(string field)
    {
        Set(field, true);
        Assert.False(Snapshot().CanRequestChanges);
        AssertError<InvalidOperationException>("BeginModification", new ModifySessionRequest("Change it"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n ")]
    public void EmptyPromptsDoNotChangeTheResult(string? prompt)
    {
        AssertError<ArgumentException>("BeginModification", new ModifySessionRequest(prompt!));
        Assert.True(Snapshot().CanRequestChanges);
        Assert.Empty(Snapshot().ModificationPrompts!);
    }

    [Fact]
    public void PromptLengthIsValidatedAndFeedbackSurvivesSnapshotSerialization()
    {
        AssertError<ArgumentException>("BeginModification", new ModifySessionRequest(new string('x', 12001)));
        Invoke("BeginModification", new ModifySessionRequest(new string('x', 12000)));
        SessionSnapshot saved = JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(Snapshot()))!;
        Assert.Equal(new string('x', 12000), Assert.Single(saved.ModificationPrompts!));
        Assert.Equal("modification_requested", _savedEvents.Last().Type);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("awaiting_approval")]
    public void UsersMayAlsoRejectFinishedPatches(string status)
    {
        Set("_status", status);
        Invoke("Reject");
        Assert.Equal("rejected", Snapshot().Status);
        Assert.False(Snapshot().CanRequestChanges);
    }

    private static TokenUsageSummary Usage(int requests) => new(
        [new(AgentNames.Developer, "test-model", requests, requests * 6, 0, requests * 4, 0, requests * 10, 0m)],
        requests, requests * 6, 0, requests * 4, 0, requests * 10, 0m);
    private object? Invoke(string name, params object[] args) => _type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_state, args);
    private void Set(string name, object value) => _type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_state, value);
    private void Add(RunEvent item) => Invoke("AddEvent", item);
    private SessionSnapshot Snapshot() => (SessionSnapshot)Invoke("Snapshot")!;
    private CancellationTokenSource Cancellation => (CancellationTokenSource)_type.GetProperty("Cancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_state)!;
    private void Finish() { Invoke("ReadyForApproval", _workspace.GetDiff()); Invoke("FinishAutomaticCompletion"); }
    private void AssertError<T>(string method, params object[] args) where T : Exception =>
        Assert.IsType<T>(Assert.Throws<TargetInvocationException>(() => Invoke(method, args)).InnerException);
    private string Git(params string[] args)
    {
        ProcessStartInfo info = RepositoryWorkspace.CreateGitStartInfo(_root,
            ["-c", "user.name=MAF Tests", "-c", "user.email=maf-tests@example.com", "-c", "commit.gpgSign=false", .. args], RepositoryProvider.GitHub, null);
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }

    public void Dispose()
    {
        Cancellation.Dispose();
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }

    private sealed class RevisionClient(RepositoryWorkspace workspace, List<string> activated) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            string agent = AgentNames.Required[activated.Count];
            activated.Add(agent);
            Assert.Contains(messages, item => item.Text.Contains("Also include a fallback", StringComparison.Ordinal));
            if (agent == AgentNames.Developer)
            {
                Assert.Equal("first result\n", workspace.ReadFile("settings.txt"));
                workspace.WriteFile("settings.txt", "first result\nfallback\n");
            }
            string text = agent == AgentNames.Reviewer ? "REVIEW_APPROVED" : agent == AgentNames.ReleaseManager ? "WORKFLOW_COMPLETE" : "Changes ready.";
            List<AIContent> contents = [new TextContent(text)];
            if (agent != AgentNames.ReleaseManager)
                contents.Add(new FunctionCallContent(Guid.NewGuid().ToString("N"),
                    options!.Tools!.Last(tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal)).Name,
                    new Dictionary<string, object?>()));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
