using Microsoft.Agents.AI;

namespace Maf.Handoff.Core;

public sealed record AgentTeam(
    AIAgent Intake,
    AIAgent? Jira,
    AIAgent? Planner,
    AIAgent Developer,
    AIAgent? Reviewer,
    AIAgent? Security,
    AIAgent ReleaseManager)
{
    public IReadOnlyList<AIAgent> All =>
        new AIAgent?[] { Intake, Jira, Planner, Developer, Reviewer, Security, ReleaseManager }
            .OfType<AIAgent>()
            .ToArray();
}
