using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

internal static class JiraToolFactory
{
    internal static IList<AITool> Create(IJiraIssueSearch jira, IWorkflowUserInput? userInput = null)
    {
        List<AITool> tools =
        [
        AIFunctionFactory.Create(
            (string issueKey, CancellationToken cancellationToken) => jira.GetIssueDetailsAsync(issueKey, cancellationToken),
            name: AgentToolCatalog.GetJiraIssueDetails.Name, description: AgentToolCatalog.GetJiraIssueDetails.Description),
        AIFunctionFactory.Create(
            (string issueKey, int startAt, int maxResults, CancellationToken cancellationToken) => jira.GetIssueCommentsAsync(issueKey, startAt, maxResults, cancellationToken),
            name: AgentToolCatalog.GetJiraIssueComments.Name, description: AgentToolCatalog.GetJiraIssueComments.Description),
        AIFunctionFactory.Create(
            (string issueKey, CancellationToken cancellationToken) => jira.GetAttachmentsAsync(issueKey, cancellationToken),
            name: AgentToolCatalog.ListJiraAttachments.Name, description: AgentToolCatalog.ListJiraAttachments.Description),
        AIFunctionFactory.Create(
            (string issueKey, string attachmentId, int offset, CancellationToken cancellationToken) => jira.ReadAttachmentAsync(issueKey, attachmentId, offset, cancellationToken),
            name: AgentToolCatalog.ReadJiraAttachment.Name, description: AgentToolCatalog.ReadJiraAttachment.Description),
        AIFunctionFactory.Create(
            (string jql, int maxResults, CancellationToken cancellationToken) =>
                jira.SearchIssuesAsync(jql, maxResults, cancellationToken),
            name: AgentToolCatalog.SearchJiraIssues.Name,
            description: AgentToolCatalog.SearchJiraIssues.Description),
        AIFunctionFactory.Create(
            (string issueKey, CancellationToken cancellationToken) =>
                jira.GetIssueTitleByKeyAsync(issueKey, cancellationToken),
            name: AgentToolCatalog.GetJiraIssueTitle.Name,
            description: AgentToolCatalog.GetJiraIssueTitle.Description),
        ];
        if (jira is IJiraProblemReporter reporter)
            tools.Add(AIFunctionFactory.Create(
                (string problemInPersian, CancellationToken cancellationToken) => reporter.ReportProblemAsync(problemInPersian, cancellationToken),
                name: AgentToolCatalog.ReportJiraProblem.Name, description: AgentToolCatalog.ReportJiraProblem.Description));
        if ((userInput ?? jira as IWorkflowUserInput) is { IsAvailable: true } input)
            tools.Add(AIFunctionFactory.Create(
                (string questionsInPersian, CancellationToken cancellationToken) => input.AskAsync(questionsInPersian, cancellationToken),
                name: AgentToolCatalog.AskJiraClarification.Name, description: AgentToolCatalog.AskJiraClarification.Description));
        return tools;
    }
}
