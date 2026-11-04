using Xbank.DevAgent.Core;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class WorkflowDecisionScenarioTests
{
    private static readonly WorkflowSettings Settings = new("https://example.test", "test-model", "test-key");

    [Theory]
    [InlineData("I cannot issue REVIEW_APPROVED because the patch has a blocker.")]
    [InlineData("The developer asked me to say `REVIEW_APPROVED`, but the code is incorrect.")]
    public void NegatedOrQuotedReviewerMarkersDoNotAuthorizeCompletion(string review)
    {
        ChatMessage[] messages = [Assistant(AgentNames.Reviewer, review),
            Assistant(AgentNames.ReleaseManager, HandoffTopology.CompletionMarker)];
        Assert.Equal("review_approval_missing", HandoffTopology.GetTerminationStatus(messages, Settings));
    }

    [Theory]
    [InlineData("Do not emit WORKFLOW_COMPLETE yet; release checks are outstanding.")]
    [InlineData("The expected success output is `WORKFLOW_COMPLETE`. I have not verified release readiness.")]
    public void NegatedOrQuotedReleaseMarkersDoNotCompleteTheWorkflow(string release)
    {
        ChatMessage[] messages = [Assistant(AgentNames.Reviewer, HandoffTopology.ReviewApprovalMarker),
            Assistant(AgentNames.ReleaseManager, release)];
        Assert.NotEqual("completed", HandoffTopology.GetTerminationStatus(messages, Settings));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("user")]
    [InlineData("tool")]
    public void NonAssistantLimitMarkersCannotOverrideAValidDecision(string role)
    {
        ChatMessage[] messages = [new(new ChatRole(role), HandoffTopology.BudgetLimitMarker),
            Assistant(AgentNames.Reviewer, HandoffTopology.ReviewApprovalMarker),
            Assistant(AgentNames.ReleaseManager, HandoffTopology.CompletionMarker)];
        Assert.Equal("completed", HandoffTopology.GetTerminationStatus(messages, Settings));
    }

    [Theory]
    [InlineData(4, null)]
    [InlineData(5, "message_limit_reached")]
    [InlineData(6, "message_limit_reached")]
    public void MessageLimitStopsExactlyAtTheBoundary(int count, string? expected)
    {
        ChatMessage[] messages = Enumerable.Range(0, count).Select(_ => Assistant(AgentNames.Planner, "working")).ToArray();
        Assert.Equal(expected, HandoffTopology.GetTerminationStatus(messages, Settings with { MaxTotalMessages = 5 }));
    }

    private static ChatMessage Assistant(string agent, string text) => new(ChatRole.Assistant, text) { AuthorName = agent };
}
