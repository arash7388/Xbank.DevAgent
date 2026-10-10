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

    private sealed class State
    {
        private readonly Type _type = typeof(RepositorySessionService).GetNestedType("SessionState", BindingFlags.NonPublic)!;
        private readonly object _state;
        public State()
        {
            CreateSessionRequest request = new("https://example.test/repo.git", "main", "TASK-42", new Dictionary<string, AgentModelConfiguration>(), null);
            _state = Activator.CreateInstance(_type, BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [Guid.NewGuid(), request, (Action<RunEvent>)(_ => { })], culture: null)!;
            _type.GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_state, "running");
        }
        public IWorkflowUserInput Input => (IWorkflowUserInput)_state;
        public CancellationTokenSource Cancellation => (CancellationTokenSource)_type.GetProperty("Cancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_state)!;
        public object? Invoke(string name, params object[] args) => _type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_state, args);
        public SessionSnapshot Snapshot() => (SessionSnapshot)Invoke("Snapshot")!;
        public void Answer(ClarifySessionRequest request) => Invoke("Answer", request);
    }
}
