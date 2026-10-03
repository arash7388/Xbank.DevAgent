using Maf.Handoff.Api;

namespace Maf.Handoff.Tests;

public sealed class TaskBranchNameTests
{
    [Theory]
    [InlineData("WEPOD-12345", "Fix the screen", "user/WEPOD-12345")]
    [InlineData("wepod-42", "Related WEPOD-99", "user/WEPOD-42")]
    [InlineData(null, "Fix WEPOD-12345: validation", "user/WEPOD-12345")]
    [InlineData(null, "See https://jira.example/browse/WEPOD-42", "user/WEPOD-42")]
    [InlineData(null, "Fix wepod-42; verify WEPOD-42", "user/WEPOD-42")]
    [InlineData(null, "Task number 12345: fix validation", "user/WEPOD-12345")]
    [InlineData(null, "task #42: fix validation", "user/WEPOD-42")]
    public void ExtractsTaskNumber(string? jiraIssueKey, string task, string expected)
        => Assert.Equal(expected, TaskBranchName.FromTask(jiraIssueKey, task));

    [Theory]
    [InlineData(null, "Fix validation")]
    [InlineData(null, "Update 12345 rows")]
    [InlineData(null, "Fix WEPOD-")]
    [InlineData(null, "Fix WEPOD-123abc")]
    [InlineData(null, "Fix WEPOD-12 and WEPOD-34")]
    [InlineData(null, "Task 12 and task 34")]
    [InlineData("WEPOD-", "Fix WEPOD-42")]
    [InlineData("OTHER-42", "Fix validation")]
    public void RejectsMissingInvalidOrAmbiguousTaskNumber(string? jiraIssueKey, string task)
        => Assert.Throws<ArgumentException>(() => TaskBranchName.FromTask(jiraIssueKey, task));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("user/WEPOD-42")]
    public void ApprovalUsesTaskBranch(string? branch)
        => Assert.Equal("user/WEPOD-42", TaskBranchName.ForApproval("WEPOD-42", "Fix validation", branch));

    [Theory]
    [InlineData("agent/change-42")]
    [InlineData("user/WEPOD-99")]
    public void ApprovalRejectsBranchOverride(string branch)
        => Assert.Throws<ArgumentException>(() => TaskBranchName.ForApproval("WEPOD-42", "Fix validation", branch));
}
