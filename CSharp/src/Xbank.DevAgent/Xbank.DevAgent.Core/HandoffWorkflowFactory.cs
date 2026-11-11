using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Core;

public static class HandoffWorkflowFactory
{
    public static WorkflowBundle Create(
        IChatClient chatClient,
        WorkflowSettings settings,
        RepositoryWorkspace? workspace = null,
        IReferenceDocumentSearch? referenceDocuments = null,
        ICodeGraphTools? codeGraphTools = null,
        CodeGraphAudit? codeGraphAudit = null,
        IToolCallStore? toolCallStore = null,
        string? workflowId = null,
        IJiraIssueSearch? jira = null,
        IWorkflowUserInput? userInput = null,
        JiraIntakeCheckpoint? jiraIntakeCheckpoint = null,
        ISqlServerTools? sqlServer = null)
    {
        AgentTeam team = AgentTeamFactory.Create(
            chatClient,
            settings,
            workspace,
            referenceDocuments,
            codeGraphTools,
            codeGraphAudit,
            toolCallStore,
            workflowId,
            jira,
            userInput,
            jiraIntakeCheckpoint,
            sqlServer);
        return Create(team, settings);
    }

    public static WorkflowBundle Create(
        IReadOnlyDictionary<string, IChatClient> clients,
        IReadOnlyDictionary<string, WorkflowSettings> settingsByAgent,
        RepositoryWorkspace? workspace = null,
        IReferenceDocumentSearch? referenceDocuments = null,
        ICodeGraphTools? codeGraphTools = null,
        CodeGraphAudit? codeGraphAudit = null,
        IToolCallStore? toolCallStore = null,
        string? workflowId = null,
        IJiraIssueSearch? jira = null,
        IWorkflowUserInput? userInput = null,
        JiraIntakeCheckpoint? jiraIntakeCheckpoint = null,
        ISqlServerTools? sqlServer = null)
    {
        AgentTeam team = AgentTeamFactory.Create(
            clients,
            settingsByAgent,
            workspace,
            referenceDocuments,
            codeGraphTools,
            codeGraphAudit,
            toolCallStore,
            workflowId,
            jira,
            userInput,
            jiraIntakeCheckpoint,
            sqlServer);
        string settingsAgent = settingsByAgent[AgentNames.Reviewer].IsMergeRequestReview ? AgentNames.Reviewer : AgentNames.Developer;
        WorkflowSettings workflowSettings = settingsByAgent.TryGetValue(settingsAgent, out WorkflowSettings? value)
            ? value
            : throw new ConfigurationException($"Model configuration for agent '{settingsAgent}' is required.");
        return Create(team, workflowSettings, team.All.Max(agent => settingsByAgent[agent.Name!].MaxAgentTurns));
    }

    private static WorkflowBundle Create(AgentTeam team, WorkflowSettings settings, int? autonomousTurnLimit = null)
    {
        AIAgent nextAfterReviewer = team.Security ?? team.ReleaseManager;

        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(team.StartAgent);

        if (settings.IsMergeRequestReview)
        {
            builder.WithHandoff(team.Jira!, team.Reviewer,
                "Route to Reviewer after gathering the linked task contents and requirements evidence.");
            builder.WithHandoff(team.Reviewer, team.ReleaseManager,
                "Route after reviewing the MR, applying and verifying applicable fixes, and recording REVIEW_APPROVED or unresolved blockers.");
        }
        else
        {
            builder.WithHandoff(team.Planner!, team.Developer!,
                "Route to the developer after the repository-backed implementation contract and acceptance criteria are complete.");

            if (team.Jira is not null)
            {
                builder.WithHandoff(team.Jira, team.Planner!,
                    "Route to the planner after task readiness, discussion, repository, and document evidence have been assessed and no blocking clarification remains.");
            }

            builder.WithHandoff(team.Developer!, team.Reviewer,
                "Route after implementation and exact diff verification to the mandatory independent reviewer.");

            builder.WithHandoff(team.Reviewer, team.Developer!,
                "Use only when a concrete blocking correctness issue in the proposed diff requires implementation changes.");
            builder.WithHandoff(team.Reviewer, nextAfterReviewer,
                "Use when correctness review passes and REVIEW_APPROVED has been recorded.");

            if (team.Security is not null)
            {
                builder.WithHandoff(team.Security, team.Developer!,
                    "Use only when the proposed diff introduces or worsens a concrete security issue that requires remediation.");
                builder.WithHandoff(team.Security, team.ReleaseManager,
                    "Use when the patch passes security review and SECURITY_APPROVED has been recorded; unrelated existing risks do not block this path.");
            }
        }

        Workflow workflow = builder
            .WithAutonomousMode(
                turnLimit: autonomousTurnLimit ?? settings.MaxAgentTurns,
                continuationPrompt: "Continue the assigned work and hand off when your role is complete.")
            .EmitAgentResponseEvents(true)
            .EmitAgentResponseUpdateEvents(true)
            .WithTerminationCondition(conversation =>
                HandoffTopology.GetTerminationStatus(conversation, settings) is not null)
            .Build();

        return new(workflow, team);
    }
}

public sealed record WorkflowBundle(Workflow Workflow, AgentTeam Team);
