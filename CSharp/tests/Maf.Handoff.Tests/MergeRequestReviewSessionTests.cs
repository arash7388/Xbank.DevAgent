using System.Reflection;
using Maf.Handoff.Api;
using Maf.Handoff.Core;

namespace Maf.Handoff.Tests;

public sealed class MergeRequestReviewSessionTests
{
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
