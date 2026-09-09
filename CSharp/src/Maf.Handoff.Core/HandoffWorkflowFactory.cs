using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

public static class HandoffWorkflowFactory
{
    public static WorkflowBundle Create(
        IChatClient chatClient,
        WorkflowSettings settings,
        RepositoryWorkspace? workspace = null,
        IReferenceDocumentSearch? referenceDocuments = null)
    {
        AgentTeam team = AgentTeamFactory.Create(chatClient, settings, workspace, referenceDocuments);
        return Create(team, settings);
    }

    public static WorkflowBundle Create(
        IReadOnlyDictionary<string, IChatClient> clients,
        IReadOnlyDictionary<string, WorkflowSettings> settingsByAgent,
        RepositoryWorkspace? workspace = null,
        IReferenceDocumentSearch? referenceDocuments = null)
    {
        AgentTeam team = AgentTeamFactory.Create(clients, settingsByAgent, workspace, referenceDocuments);
        WorkflowSettings workflowSettings = settingsByAgent.TryGetValue(AgentNames.Intake, out WorkflowSettings? value)
            ? value
            : throw new ConfigurationException("Model configuration for agent 'intake' is required.");
        return Create(team, workflowSettings);
    }

    private static WorkflowBundle Create(AgentTeam team, WorkflowSettings settings)
    {
        AIAgent planningOrImplementationStage = team.Planner ?? team.Developer;
        AIAgent firstImplementationStage = team.Jira ?? planningOrImplementationStage;
        AIAgent nextAfterDeveloper = team.Reviewer ?? team.Security ?? team.ReleaseManager;
        AIAgent nextAfterReviewer = team.Security ?? team.ReleaseManager;

        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(team.Intake)
            .WithHandoff(team.Intake, firstImplementationStage,
                team.Planner is null
                    ? "Route every accepted change request directly to the developer."
                    : "Route every accepted change request to the planner for repository inspection and an implementation plan.");

        if (team.Planner is not null)
        {
            builder.WithHandoff(team.Planner, team.Developer,
                "Route to the developer after the repository-backed plan and acceptance criteria are complete.");
        }

        if (team.Jira is not null)
        {
            builder.WithHandoff(team.Jira, planningOrImplementationStage,
                team.Planner is null
                    ? "Route to the developer after the requested Jira context has been retrieved."
                    : "Route to the planner after the requested Jira context has been retrieved and summarized.");
        }

        builder.WithHandoff(team.Developer, nextAfterDeveloper,
            "Route after implementation and exact diff verification to the next enabled quality or release gate.");

        if (team.Reviewer is not null)
        {
            builder.WithHandoff(team.Reviewer, team.Developer,
                "Use only when a concrete blocking correctness issue in the proposed diff requires implementation changes.");
            builder.WithHandoff(team.Reviewer, nextAfterReviewer,
                "Use when correctness review passes and REVIEW_APPROVED has been recorded.");
        }

        if (team.Security is not null)
        {
            builder.WithHandoff(team.Security, team.Developer,
                "Use only when the proposed diff introduces or worsens a concrete security issue that requires remediation.");
            builder.WithHandoff(team.Security, team.ReleaseManager,
                "Use when the patch passes security review and SECURITY_APPROVED has been recorded; unrelated existing risks do not block this path.");
        }

        if (team.Planner is not null)
        {
            builder.WithHandoff(team.ReleaseManager, team.Planner,
                "Use only when a blocking design or acceptance-criteria gap requires a revised plan.");
        }

        Workflow workflow = builder
            .WithHandoff(team.ReleaseManager, team.Developer,
                "Use only when a blocking implementation or diff issue requires a code change.")
            .WithAutonomousMode(
                turnLimit: settings.MaxAgentTurns,
                continuationPrompt: "Continue the assigned work and hand off when your role is complete.")
            .EmitAgentResponseEvents(true)
            .EmitAgentResponseUpdateEvents(true)
            .WithTerminationCondition(conversation =>
                conversation.Count >= settings.MaxTotalMessages ||
                HandoffTopology.HasExceededReviewerRemediationCycles(
                    conversation.Select(message => message.AuthorName),
                    settings.MaxReviewerRemediationCycles) ||
                conversation.Any(message =>
                    HandoffTopology.IsCompletionMessage(message.AuthorName, message.Text)))
            .Build();

        return new(workflow, team);
    }
}

public sealed record WorkflowBundle(Workflow Workflow, AgentTeam Team);
