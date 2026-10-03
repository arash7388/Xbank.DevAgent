using Maf.Handoff.Core;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class HandoffTopologyTests
{
    [Theory]
    [InlineData(HandoffTopology.RequestLimitMarker, "request_limit_reached")]
    [InlineData(HandoffTopology.AgentTurnLimitMarker, "agent_turn_limit_reached")]
    [InlineData(HandoffTopology.ReviewerRemediationLimitMarker, "reviewer_remediation_limit_reached")]
    [InlineData(HandoffTopology.ReleaseRejectionMarker, "rejected_by_release")]
    public void StopsAndRejectionOverrideConflictingApproval(string marker, string expected)
    {
        ChatMessage[] transcript =
        [new(ChatRole.Assistant, $"{HandoffTopology.CompletionMarker} {marker}") { AuthorName = AgentNames.ReleaseManager }];

        Assert.Equal(expected, HandoffTopology.GetTerminationStatus(transcript, new("https://example.test", "model", "key")));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("tool")]
    public void NonAssistantMessagesCannotApproveEvenWithReleaseAuthor(string role)
    {
        ChatMessage[] transcript =
        [new(new ChatRole(role), HandoffTopology.CompletionMarker) { AuthorName = AgentNames.ReleaseManager }];

        Assert.Null(HandoffTopology.GetTerminationStatus(transcript, new("https://example.test", "model", "key")));
    }

    [Fact]
    public void ToolAuthorsAndContinuationPromptsDoNotHideReviewCycles()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.Assistant, "Fix A") { AuthorName = "reviewer" },
            new(ChatRole.Tool, "Transferred") { AuthorName = "handoff_to_developer" },
            new(ChatRole.Assistant, "Fixed A") { AuthorName = "software-delivery-developer" },
            new(ChatRole.Assistant, "Fix B") { AuthorName = "software-delivery-reviewer" },
            new(ChatRole.User, "Continue") { AuthorName = "user" },
            new(ChatRole.Assistant, "Fixed B") { AuthorName = "developer" },
        ];

        Assert.True(HandoffTopology.HasExceededReviewerRemediationCycles(messages, 1));
        Assert.False(HandoffTopology.HasExceededReviewerRemediationCycles(messages, 2));
    }

    [Fact]
    public void JiraRoutesThroughPlanner()
    {
        Assert.Equal(["planner"], HandoffTopology.Edges["jira"]);
    }

    [Fact]
    public void DeveloperCannotBypassReviewer()
    {
        IReadOnlyList<string> targets = HandoffTopology.Edges["developer"];

        Assert.Equal(["reviewer"], targets);
    }

    [Fact]
    public void SecurityCanRemediateOrRelease()
    {
        IReadOnlyList<string> targets = HandoffTopology.Edges["security"];

        Assert.Equal(["developer", "release_manager"], targets);
    }

    [Fact]
    public void EveryTargetIsARegisteredAgent()
    {
        HashSet<string> agents = [.. HandoffTopology.Edges.Keys];

        Assert.All(
            HandoffTopology.Edges.Values.SelectMany(targets => targets),
            target => Assert.Contains(target, agents));
    }

    [Theory]
    [InlineData("release_manager")]
    [InlineData("software-delivery-release_manager")]
    public void RecognizesReleaseCompletionFromAgentNameOrId(string authorName)
    {
        Assert.True(HandoffTopology.IsCompletionMessage(authorName, "Ready. WORKFLOW_COMPLETE"));
    }

    [Fact]
    public void DoesNotAcceptCompletionFromAnotherAgent()
    {
        Assert.False(HandoffTopology.IsCompletionMessage("developer", "WORKFLOW_COMPLETE"));
    }

    [Fact]
    public void RecognizesRequestLimitMarkerFromAnyAgent()
    {
        Assert.True(HandoffTopology.IsRequestLimitMessage(
            "WORKFLOW_REQUEST_LIMIT_REACHED No more provider calls are allowed."));
    }

    [Fact]
    public void ReleaseManagerIsTerminal()
    {
        Assert.Empty(HandoffTopology.Edges[AgentNames.ReleaseManager]);
        Assert.True(HandoffTopology.IsReleaseRejectionMessage(
            AgentNames.ReleaseManager,
            "Concrete blocker. WORKFLOW_REJECTED"));
    }

    [Fact]
    public void AllowsConfiguredReviewerRemediationCycles()
    {
        string?[] authors = ["reviewer", "developer", "reviewer", "developer"];

        Assert.False(HandoffTopology.HasExceededReviewerRemediationCycles(authors, maximumCycles: 2));
    }

    [Fact]
    public void StopsTheThirdReviewerRemediationCycle()
    {
        string?[] authors =
            ["reviewer", "developer", "reviewer", "developer", "reviewer", "developer"];

        Assert.True(HandoffTopology.HasExceededReviewerRemediationCycles(authors, maximumCycles: 2));
    }
}
