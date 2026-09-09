using Maf.Handoff.Core;

namespace Maf.Handoff.Tests;

public sealed class HandoffTopologyTests
{
    [Fact]
    public void IntakeRoutesThroughJira()
    {
        Assert.Equal(["jira"], HandoffTopology.Edges["intake"]);
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
