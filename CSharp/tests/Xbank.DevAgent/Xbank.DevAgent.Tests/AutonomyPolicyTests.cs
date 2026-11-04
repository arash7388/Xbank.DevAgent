using Xbank.DevAgent.Core;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class AutonomyPolicyTests
{
    [Theory]
    [InlineData("ask_user_decision")]
    [InlineData("ask_jira_clarification")]
    public void HumanDecisionRequestsAreNeverReusableCheckpoints(string tool)
    {
        ChatResponse response = new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("decision", tool, new Dictionary<string, object?> { ["questions"] = "Which approach?" })]));
        Assert.False(CheckpointSerialization.SerializeResponse(response).Reusable);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(75)]
    [InlineData(100)]
    public void ValidLevelsReachAgentInstructions(int level)
    {
        WorkflowSettings settings = new("https://example.test", "model", "key") { AutonomyLevel = level };
        settings.Validate();
        var options = AgentTeamFactory.CreateChatOptions(settings, "Role guidance", null);
        Assert.Contains($"{level}%", options.Instructions);
        Assert.Contains("takes precedence", options.Instructions);
        Assert.Contains("Publishing is controlled separately", options.Instructions);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(101)]
    public void InvalidLevelsAreRejected(int level) => Assert.Throws<ConfigurationException>(() =>
        new WorkflowSettings("https://example.test", "model", "key") { AutonomyLevel = level }.Validate());

    [Fact]
    public async Task FullyAutonomousInputNeverWaitsForAHumanAndRecordsTheQuestion()
    {
        Input inner = new();
        string? logged = null;
        AutonomyUserInput input = new(100, inner, question => logged = question);
        string answer = await input.AskAsync("Which approach?");
        Assert.Equal(AutonomyPolicy.DelegatedDecision, answer);
        Assert.Equal("Which approach?", logged);
        Assert.Equal(0, inner.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(75)]
    public async Task InteractiveLevelsWaitForTheActualAnswer(int level)
    {
        Input inner = new();
        AutonomyUserInput input = new(level, inner);
        Task<string> pending = input.AskAsync("Which approach?");
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, inner.Calls);
        inner.Answer.SetResult("Use the existing pattern");
        Assert.Equal("Use the existing pattern", await pending);
    }

    [Fact]
    public async Task FullAutonomyStillHonorsCancellation()
    {
        Input inner = new();
        AutonomyUserInput input = new(100, inner);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await input.AskAsync("Question", cancellation.Token));
        Assert.Equal(0, inner.Calls);
    }

    private sealed class Input : IWorkflowUserInput
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<string> Answer { get; } = new();
        public Task<string> AskAsync(string questions, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Answer.Task.WaitAsync(cancellationToken);
        }
    }
}
