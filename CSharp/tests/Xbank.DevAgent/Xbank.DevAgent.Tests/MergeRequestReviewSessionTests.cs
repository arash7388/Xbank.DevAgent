using System.Reflection;
using Xbank.DevAgent.Api;
using Xbank.DevAgent.Core;

namespace Xbank.DevAgent.Tests;

public sealed class MergeRequestReviewSessionTests
{
    [Theory]
    [InlineData(MergeRequestReviewModes.Independent)]
    [InlineData(MergeRequestReviewModes.Feedback)]
    [InlineData(MergeRequestReviewModes.Combined)]
    public void EveryReviewModeFiltersOutOtherAgentConfigurations(string mode)
    {
        var agents = AgentNames.All.ToDictionary(name => name, _ => (AgentModelConfiguration)null!);
        CreateSessionRequest request = new("https://gitlab.com/acme/orders.git", "main", "Review", agents, null,
            RunMode: "gitlab_review", MrReviewMode: mode);
        GitLabMergeRequestReview review = new(17, "url", "Task", "feature", "main", "{}");
        var prepared = PrepareReviewRequest(request, review);
        Assert.Equal(AgentNames.ReviewRequired.Order(), prepared.Agents.Keys.Order());
        Assert.Equal(AgentNames.All.Count, agents.Count);
    }

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
    public void ReviewInstructionsRequireJiraIntakeAndCompletePinnedDiffCoverage()
    {
        GitLabMergeRequestReview review = new(31, "https://gitlab.com/acme/orders/-/merge_requests/31",
            "Fix checkout", "fix/checkout", "main", "{}");
        string instructions = review.WorkflowInstructions();
        Assert.Contains("read the full linked Jira task contents", instructions);
        Assert.Contains("before", instructions);
        Assert.Contains("Review all MR commits and", instructions);
        Assert.Contains("every changed file", instructions);
        Assert.Contains("diff_refs.base_sha", instructions);
        Assert.Contains("pre-existing issues", instructions);
        Assert.Contains("no fixes can still pass", instructions);
    }

    [Theory]
    [InlineData(null, "WEPOD-123", "Expected behavior", "WEPOD-123")]
    [InlineData("OTHER-9", "WEPOD-123", "Expected behavior", "OTHER-9")]
    [InlineData(null, "fix/checkout", "See WEPOD-456", "WEPOD-456")]
    [InlineData(null, "fix/checkout", "See WEPOD-456 and OTHER-9", null)]
    public void ReviewRequestRetainsJiraSelectionsAndResolvesOnlyUnambiguousMetadata(
        string? selected, string branch, string description, string? expected)
    {
        var agents = new Dictionary<string, AgentModelConfiguration> { [AgentNames.Jira] = null! };
        CreateSessionRequest request = new("https://gitlab.com/acme/orders.git", "main", "", agents, null,
            JiraIssueKey: selected, JiraAttachmentIds: selected is null ? null : ["42"], JiraIncludeComments: false);
        GitLabMergeRequestReview review = new(31, "https://gitlab.com/acme/orders/-/merge_requests/31",
            "Fix checkout", branch, "main", System.Text.Json.JsonSerializer.Serialize(new { description }));
        CreateSessionRequest prepared = PrepareReviewRequest(request, review);
        Assert.Equal(expected, prepared.JiraIssueKey);
        Assert.Equal(request.JiraAttachmentIds, prepared.JiraAttachmentIds);
        Assert.False(prepared.JiraIncludeComments);
        Assert.Equal(branch, prepared.BaseBranch);
    }

    [Fact]
    public void ReviewRequestResolvesIssueFromCommitMessageAndRequiresJiraAgent()
    {
        CreateSessionRequest request = new("https://gitlab.com/acme/orders.git", "main", "",
            new Dictionary<string, AgentModelConfiguration> { [AgentNames.Jira] = null! }, null);
        GitLabMergeRequestReview review = new(31, "https://gitlab.com/acme/orders/-/merge_requests/31",
            "Fix checkout", "fix/checkout", "main", "{}")
            { Commits = [new("sha", "Fix checkout", "WEPOD-789 Fix checkout")] };
        Assert.Equal("WEPOD-789", PrepareReviewRequest(request, review).JiraIssueKey);
        var error = Assert.Throws<TargetInvocationException>(() => PrepareReviewRequest(
            request with { Agents = new Dictionary<string, AgentModelConfiguration>() }, review));
        Assert.IsType<ArgumentException>(error.InnerException);
        Assert.Contains("Jira agent", error.InnerException.Message);
    }

    private static CreateSessionRequest PrepareReviewRequest(CreateSessionRequest request, GitLabMergeRequestReview review) =>
        (CreateSessionRequest)typeof(RepositorySessionService).GetMethod("PrepareReviewRequest", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [request, review])!;

    [Theory]
    [InlineData(MergeRequestReviewModes.Independent, true, true)]
    [InlineData(MergeRequestReviewModes.Combined, true, true)]
    [InlineData(MergeRequestReviewModes.Feedback, true, false)]
    [InlineData(MergeRequestReviewModes.Independent, false, false)]
    [InlineData(MergeRequestReviewModes.Combined, false, false)]
    public void SourceCommentsAreOptInAndLimitedToIndependentReview(string mode, bool enabled, bool expected)
    {
        GitLabMergeRequestReview review = new(17, "https://gitlab.com/acme/orders/-/merge_requests/17",
            "Fix checkout", "fix/checkout", "main", "context");
        string instructions = review.WorkflowInstructions(mode, enabled);
        Assert.Equal(expected, instructions.Contains("Source-code issue comments are enabled", StringComparison.Ordinal));
        if (expected)
        {
            Assert.Contains("exact prefix \"Agent Review:\"", instructions);
            Assert.Contains("relevant source-code location", instructions);
            Assert.Contains("supplement the required final report", instructions);
            Assert.Contains("Reviewer verifies", instructions);
            Assert.Contains("duplicate existing Agent Review comments", instructions);
            Assert.Contains("describe the fix or invariant accurately", instructions);
        }
        else
            Assert.Contains("Do not add Agent Review annotations", instructions);
        Assert.EndsWith(review.Context, instructions);
    }

    [Fact]
    public void SourceCommentsDefaultToDisabledForApiRequests()
    {
        CreateSessionRequest request = new("https://gitlab.com/acme/orders.git", "fix/checkout", "Review",
            new Dictionary<string, AgentModelConfiguration>(), null);
        Assert.False(request.CommentIssuesOnSourceCode);
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
        Assert.Contains("Reviewer remediation and configured build policy", instructions);
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
