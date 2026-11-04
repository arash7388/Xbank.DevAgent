using Xbank.DevAgent.Core;

namespace Xbank.DevAgent.Tests;

public sealed class ReviewerRemediationBudgetTests
{
    [Fact]
    public void ToolRoundsWithinAnActivationDoNotUseRemediationCycles()
    {
        ReviewerRemediationBudget budget = new(1);
        Assert.True(budget.TryActivate(AgentNames.Developer));
        Assert.True(budget.TryActivate(AgentNames.Reviewer));
        Assert.True(budget.TryActivate(AgentNames.Reviewer));
        Assert.True(budget.TryActivate(AgentNames.Developer));
        Assert.True(budget.TryActivate(AgentNames.Developer));
        Assert.True(budget.TryActivate(AgentNames.Reviewer));
        Assert.False(budget.TryActivate(AgentNames.Developer));
        Assert.False(budget.TryActivate(AgentNames.Developer));
        Assert.False(budget.TryActivate(AgentNames.ReleaseManager));
    }

    [Fact]
    public void SecurityReturnsAreNotCountedAsReviewerReturns()
    {
        ReviewerRemediationBudget budget = new(1);
        Assert.True(budget.TryActivate(AgentNames.Reviewer));
        Assert.True(budget.TryActivate(AgentNames.Security));
        Assert.True(budget.TryActivate(AgentNames.Developer));
        Assert.True(budget.TryActivate(AgentNames.Reviewer));
        Assert.True(budget.TryActivate(AgentNames.Developer));
    }
}
