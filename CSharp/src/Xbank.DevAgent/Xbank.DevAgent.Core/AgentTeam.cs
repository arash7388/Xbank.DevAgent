using Microsoft.Agents.AI;

namespace Xbank.DevAgent.Core;

public sealed record AgentTeam(
    AIAgent? Jira,
    AIAgent? Planner,
    AIAgent? Developer,
    AIAgent Reviewer,
    AIAgent? Security,
    AIAgent ReleaseManager)
{
    public AIAgent StartAgent => Jira ?? Planner ?? Reviewer;

    public IReadOnlyList<AIAgent> All =>
        new AIAgent?[] { Jira, Planner, Developer, Reviewer, Security, ReleaseManager }
            .OfType<AIAgent>()
            .ToArray();
}
