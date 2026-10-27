using System.Reflection;
using Maf.Handoff.Api;
using Maf.Handoff.Core;

namespace Maf.Handoff.Tests;

public sealed class MergeRequestReviewSessionTests
{
    [Theory]
    [InlineData(MergeRequestReviewModes.Independent)]
    [InlineData(MergeRequestReviewModes.Feedback)]
    [InlineData(MergeRequestReviewModes.Combined)]
    public void ReviewModeControlsInstructions(string mode)
    {
        GitLabMergeRequestReview review = new(17, "https://gitlab.com/acme/orders/-/merge_requests/17",
            "Fix checkout", "fix/checkout", "main", "context");
        string instructions = review.WorkflowInstructions(mode);
        Assert.Equal(mode != MergeRequestReviewModes.Feedback,
            instructions.Contains("Independently review the implementation", StringComparison.Ordinal));
        Assert.Equal(mode != MergeRequestReviewModes.Independent,
            instructions.Contains("apply applicable feedback", StringComparison.Ordinal));
        Assert.Contains("configured build", instructions);
        Assert.EndsWith(review.Context, instructions);
        if (mode == MergeRequestReviewModes.Independent)
            Assert.Contains("Do not retrieve or use them", instructions);
        if (mode == MergeRequestReviewModes.Feedback)
            Assert.Contains("Do not perform a separate independent requirements audit", instructions);
    }

    [Fact]
    public void InvalidReviewModeIsRejected()
    {
        Assert.Throws<ArgumentException>(() => MergeRequestReviewModes.Validate("unknown"));
    }

    [Fact]
    public void ReviewInstructionsRequireFeedbackAndIndependentRequirementsReviewWithoutDiscussions()
    {
        GitLabMergeRequestReview review = new(17, "https://gitlab.com/acme/orders/-/merge_requests/17",
            "Fix checkout", "fix/checkout", "main", """{"description":"Reject empty carts","discussions":[]}""");

        string instructions = review.WorkflowInstructions();

        Assert.Contains("apply applicable feedback", instructions);
        Assert.Contains("Independently review the implementation against the task requirements and acceptance criteria", instructions);
        Assert.Contains("even when", instructions);
        Assert.Contains("there are no comments or unresolved threads", instructions);
        Assert.Contains("normal developer/reviewer cycle and configured build policy", instructions);
        Assert.Contains("separately report GitLab feedback outcomes and independent requirements", instructions);
        Assert.Contains("no discussion mutations are authorized", instructions);
        Assert.EndsWith(review.Context, instructions);
    }

    [Fact]
    public void ReviewSessionUsesSourceBranchWithoutTaskNumberAndLinksExistingMr()
    {
        Type type = typeof(RepositorySessionService).GetNestedType("SessionState", BindingFlags.NonPublic)!;
        CreateSessionRequest request = new("https://gitlab.com/acme/orders.git", "fix/checkout", "Address review feedback",
            new Dictionary<string, AgentModelConfiguration>(), null, RunMode: "gitlab_review", MergeRequestIid: 17);
        List<RunEvent> events = [];
        object state = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [Guid.NewGuid(), request, (Action<RunEvent>)events.Add], null)!;
        string url = "https://gitlab.com/acme/orders/-/merge_requests/17";
        type.GetProperty("MergeRequestReview", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(state,
            new GitLabMergeRequestReview(17, url, "Fix checkout", "fix/checkout", "main", "review context"));
        SessionSnapshot snapshot = (SessionSnapshot)type.GetMethod("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, null)!;
        Assert.Equal("gitlab_review", snapshot.RunMode);
        Assert.Equal("fix/checkout", snapshot.Branch);
        Assert.Equal("fix/checkout", snapshot.BaseBranch);
        Assert.Equal(url, snapshot.PullRequest!.Url);
        Assert.Null(snapshot.Approval);
    }
}
