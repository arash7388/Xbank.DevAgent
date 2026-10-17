using System.Reflection;
using Maf.Handoff.Api;
using Maf.Handoff.Core;

namespace Maf.Handoff.Tests;

public sealed class SessionClarificationTests
{
    [Fact]
    public async Task AnswersResumeSameSessionAndFollowUpGetsNewQuestionId()
    {
        State state = new();
        Task<string> pending = state.Input.AskAsync("Which value?", state.Cancellation.Token);
        SessionSnapshot waiting = state.Snapshot();
        Assert.False(pending.IsCompleted);
        Assert.Equal("awaiting_input", waiting.Status);
        Assert.Equal("Which value?", waiting.Clarification!.Questions);
        state.Answer(new(waiting.Clarification.Id, "  Use 7  "));
        Assert.Equal("Use 7", await pending);
        SessionSnapshot resumed = state.Snapshot();
        Assert.Equal(waiting.Id, resumed.Id);
        Assert.Equal(waiting.Task, resumed.Task);
        Assert.Equal("running", resumed.Status);
        Assert.Null(resumed.Clarification);
        Assert.Equal("Use 7", Assert.Single(resumed.Events, item => item.Type == "clarification_answered").Text);
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => state.Answer(new(waiting.Clarification.Id, "Duplicate"))).InnerException);

        pending = state.Input.AskAsync("Which environment?", state.Cancellation.Token);
        Guid next = state.Snapshot().Clarification!.Id;
        Assert.NotEqual(waiting.Clarification.Id, next);
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => state.Answer(new(waiting.Clarification.Id, "Stale"))).InnerException);
        Assert.IsType<ArgumentException>(Assert.Throws<TargetInvocationException>(() => state.Answer(new(next, "  "))).InnerException);
        Assert.False(pending.IsCompleted);
        state.Answer(new(next, "Staging"));
        Assert.Equal("Staging", await pending);
    }

    [Fact]
    public async Task CancellationUnblocksWaitingAgent()
    {
        State state = new();
        Task<string> pending = state.Input.AskAsync("Which value?", state.Cancellation.Token);
        state.Invoke("Cancel");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("cancelled", state.Snapshot().Status);
        Assert.Null(state.Snapshot().Clarification);
    }

    [Fact]
    public async Task FullyAutonomousSessionCannotEnterAwaitingInput()
    {
        State state = new(100);
        Assert.Equal(AutonomyPolicy.DelegatedDecision, await state.Input.AskAsync("Choose an approach"));
        SessionSnapshot snapshot = state.Snapshot();
        Assert.Equal("running", snapshot.Status);
        Assert.Null(snapshot.Clarification);
        Assert.Equal(100, snapshot.AutonomyLevel);
        Assert.Contains(snapshot.Events, item => item.Type == "autonomy_decision");
    }

    [Theory]
    [InlineData(50, false, "awaiting_approval", false)]
    [InlineData(100, false, "completed", false)]
    [InlineData(100, true, "awaiting_approval", true)]
    [InlineData(25, true, "awaiting_approval", true)]
    public void CompletionPolicyDistinguishesFinishedPatchesAndAutomaticPublishing(int level, bool autoCommit, string expected, bool pending)
    {
        State state = new(level, autoCommit);
        state.Invoke("ReadyForApproval", "patch");
        Assert.Equal(expected, state.Snapshot().Status);
        Assert.Equal(pending, state.Snapshot().AutomaticCompletionPending);
        state.Invoke("FinishAutomaticCompletion");
        Assert.False(state.Snapshot().AutomaticCompletionPending);
    }

    [Fact]
    public void FullyAutonomousNoChangeRunDoesNotPublish()
    {
        State state = new(100, true);
        state.Invoke("ReadyForApproval", "");
        Assert.Equal("no_changes", state.Snapshot().Status);
        Assert.False(state.Snapshot().AutomaticCompletionPending);
    }

    [Fact]
    public void StoppedRunCannotBecomeFailedOrReadyForApproval()
    {
        State state = new();
        state.Invoke("Cancel");
        state.Invoke("MarkFailed", "Late provider error", "late patch");
        state.Invoke("ReadyForApproval", "late patch");
        state.Invoke("Cancel");
        Assert.Equal("cancelled", state.Snapshot().Status);
        Assert.Null(state.Snapshot().Error);
        Assert.IsType<OperationCanceledException>(Assert.Throws<TargetInvocationException>(
            () => state.Invoke("BeginAgentRun", new object[] { null! })).InnerException);
        Assert.Equal("cancelled", state.Snapshot().Status);
    }

    [Theory]
    [InlineData("awaiting_approval")]
    [InlineData("approving")]
    [InlineData("committed")]
    [InlineData("pushed")]
    [InlineData("failed")]
    public void StopCannotOverwriteAnInactiveRun(string status)
    {
        State state = new();
        state.SetStatus(status);
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(
            () => state.Invoke("Cancel")).InnerException);
        Assert.Equal(status, state.Snapshot().Status);
        Assert.False(state.Cancellation.IsCancellationRequested);
    }

    private sealed class State
    {
        private readonly Type _type = typeof(RepositorySessionService).GetNestedType("SessionState", BindingFlags.NonPublic)!;
        private readonly object _state;
        public State(int autonomyLevel = 50, bool autoCommit = false)
        {
            CreateSessionRequest request = new("https://example.test/repo.git", "main", "TASK-42", new Dictionary<string, AgentModelConfiguration>(), null,
                AutonomyLevel: autonomyLevel, AutoCommit: autoCommit);
            _state = Activator.CreateInstance(_type, BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [Guid.NewGuid(), request, (Action<RunEvent>)(_ => { })], culture: null)!;
            _type.GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_state, "running");
        }
        public IWorkflowUserInput Input => (IWorkflowUserInput)_state;
        public void SetStatus(string status) => _type.GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_state, status);
        public CancellationTokenSource Cancellation => (CancellationTokenSource)_type.GetProperty("Cancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_state)!;
        public object? Invoke(string name, params object[] args) => _type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_state, args);
        public SessionSnapshot Snapshot() => (SessionSnapshot)Invoke("Snapshot")!;
        public void Answer(ClarifySessionRequest request) => Invoke("Answer", request);
    }
}
