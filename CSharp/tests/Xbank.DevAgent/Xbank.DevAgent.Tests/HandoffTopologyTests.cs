using Xbank.DevAgent.Core;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class HandoffTopologyTests
{
    [Theory]
    [InlineData("jira", "assistant", false, "input_required")]
    [InlineData("software-delivery-jira", "assistant", false, "input_required")]
    [InlineData("jira", "user", false, null)]
    [InlineData("jira", "tool", false, null)]
    [InlineData("planner", "assistant", false, null)]
    [InlineData("jira", "assistant", true, null)]
    public void OnlyACompletedJiraAssistantResponseCanRequestClarification(
        string author, string role, bool pendingTools, string? expected)
    {
        ChatMessage message = new(new ChatRole(role), HandoffTopology.JiraClarificationMarker) { AuthorName = author };
        if (pendingTools)
            message.Contents.Add(new FunctionCallContent("comments", AgentToolCatalog.GetJiraIssueComments.Name,
                new Dictionary<string, object?> { ["issueKey"] = "WEPOD-42", ["startAt"] = 0, ["maxResults"] = 50 }));

        Assert.Equal(expected, HandoffTopology.GetTerminationStatus([message], new("https://example.test", "model", "key")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrWithdrawnReviewerApprovalCannotComplete(bool previouslyApproved)
    {
        List<ChatMessage> transcript = [];
        if (previouslyApproved)
            transcript.Add(new(ChatRole.Assistant, HandoffTopology.ReviewApprovalMarker) { AuthorName = AgentNames.Reviewer });
        transcript.Add(new(ChatRole.Assistant, "Blocking correctness issue") { AuthorName = AgentNames.Reviewer });
        transcript.Add(new(ChatRole.Assistant, HandoffTopology.CompletionMarker) { AuthorName = AgentNames.ReleaseManager });

        Assert.Equal("review_approval_missing",
            HandoffTopology.GetTerminationStatus(transcript, new("https://example.test", "model", "key")));
    }

    [Theory]
    [InlineData("reviewer", "assistant", true)]
    [InlineData("software-delivery-reviewer", "assistant", true)]
    [InlineData("developer", "assistant", false)]
    [InlineData("release_manager", "assistant", false)]
    [InlineData("reviewer", "tool", false)]
    [InlineData("reviewer", "user", false)]
    public void CompletionRequiresIndependentReviewerApproval(string author, string role, bool approved)
    {
        ChatMessage[] transcript =
        [
            new(ChatRole.Assistant, "Implemented") { AuthorName = AgentNames.Developer },
            new(new ChatRole(role), HandoffTopology.ReviewApprovalMarker) { AuthorName = author },
            new(ChatRole.Assistant, HandoffTopology.CompletionMarker) { AuthorName = AgentNames.ReleaseManager },
        ];

        Assert.Equal(approved ? "completed" : "review_approval_missing",
            HandoffTopology.GetTerminationStatus(transcript, new("https://example.test", "model", "key")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeveloperRemediationRequiresFreshReviewerApproval(bool reviewedAgain)
    {
        List<ChatMessage> transcript =
        [
            new(ChatRole.Assistant, HandoffTopology.ReviewApprovalMarker) { AuthorName = AgentNames.Reviewer },
            new(ChatRole.Assistant, "Remediated security finding") { AuthorName = AgentNames.Developer },
        ];
        if (reviewedAgain)
            transcript.Add(new(ChatRole.Assistant, HandoffTopology.ReviewApprovalMarker) { AuthorName = AgentNames.Reviewer });
        transcript.Add(new(ChatRole.Assistant, HandoffTopology.CompletionMarker) { AuthorName = AgentNames.ReleaseManager });

        Assert.Equal(reviewedAgain ? "completed" : "review_approval_missing",
            HandoffTopology.GetTerminationStatus(transcript, new("https://example.test", "model", "key")));
    }

    [Theory]
    [InlineData(HandoffTopology.RequestLimitMarker, "request_limit_reached")]
    [InlineData(HandoffTopology.AgentTurnLimitMarker, "agent_turn_limit_reached")]
    [InlineData(HandoffTopology.ReviewerRemediationLimitMarker, "reviewer_remediation_limit_reached")]
    [InlineData(HandoffTopology.LoopDetectedMarker, "loop_detected")]
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
