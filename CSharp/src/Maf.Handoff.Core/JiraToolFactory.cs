using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

internal static class JiraToolFactory
{
    internal static IList<AITool> Create(IJiraIssueSearch jira) =>
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
}
