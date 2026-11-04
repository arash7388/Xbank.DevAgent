using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Core;

internal static class JiraToolFactory
{
    internal static IList<AITool> Create(IJiraIssueSearch jira, IWorkflowUserInput? userInput = null, bool validationEnabled = true)
    {
        List<AITool> tools =
        [
        AIFunctionFactory.Create(
            (string issueKey, CancellationToken cancellationToken) => WithIssueKeyAsync(issueKey, key => jira.GetIssueDetailsAsync(key, cancellationToken)),
            name: AgentToolCatalog.GetJiraIssueDetails.Name, description: AgentToolCatalog.GetJiraIssueDetails.Description),
        AIFunctionFactory.Create(
            (string issueKey, int startAt, int maxResults, CancellationToken cancellationToken) => WithIssueKeyAsync(issueKey, key => jira.GetIssueCommentsAsync(key, startAt, maxResults, cancellationToken)),
            name: AgentToolCatalog.GetJiraIssueComments.Name, description: AgentToolCatalog.GetJiraIssueComments.Description),
        AIFunctionFactory.Create(
            (string issueKey, CancellationToken cancellationToken) => WithIssueKeyAsync(issueKey, key => jira.GetAttachmentsAsync(key, cancellationToken)),
            name: AgentToolCatalog.ListJiraAttachments.Name, description: AgentToolCatalog.ListJiraAttachments.Description),
        AIFunctionFactory.Create(
            (string issueKey, string attachmentId, int offset, CancellationToken cancellationToken) => WithIssueKeyAsync(issueKey, key => jira.ReadAttachmentAsync(key, attachmentId, offset, cancellationToken)),
            name: AgentToolCatalog.ReadJiraAttachment.Name, description: AgentToolCatalog.ReadJiraAttachment.Description),
        AIFunctionFactory.Create(
            (string jql, int maxResults, CancellationToken cancellationToken) =>
                jira.SearchIssuesAsync(jql, maxResults, cancellationToken),
            name: AgentToolCatalog.SearchJiraIssues.Name,
            description: AgentToolCatalog.SearchJiraIssues.Description),
        AIFunctionFactory.Create(
            (string issueKey, CancellationToken cancellationToken) =>
                WithIssueKeyAsync(issueKey, key => jira.GetIssueTitleByKeyAsync(key, cancellationToken)),
            name: AgentToolCatalog.GetJiraIssueTitle.Name,
            description: AgentToolCatalog.GetJiraIssueTitle.Description),
        ];
        if (validationEnabled && jira is IJiraProblemReporter reporter)
            tools.Add(AIFunctionFactory.Create(
                (string problemInPersian, CancellationToken cancellationToken) => reporter.ReportProblemAsync(problemInPersian, cancellationToken),
                name: AgentToolCatalog.ReportJiraProblem.Name, description: AgentToolCatalog.ReportJiraProblem.Description));
        if (validationEnabled && (userInput ?? jira as IWorkflowUserInput) is { IsAvailable: true } input)
            tools.Add(AIFunctionFactory.Create(
                (string questionsInPersian, CancellationToken cancellationToken) => input.AskAsync(questionsInPersian, cancellationToken),
                name: AgentToolCatalog.AskJiraClarification.Name, description: AgentToolCatalog.AskJiraClarification.Description));
        return tools;
    }

    private static async Task<object?> WithIssueKeyAsync<T>(string? issueKey, Func<string, Task<T>> read)
    {
        string key = issueKey?.Trim() ?? string.Empty;
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, @"\A[A-Za-z][A-Za-z0-9_]*-[0-9]+\z"))
            return new
            {
                error = "invalid_issue_key",
                message = "Provide a full Jira issue key such as WEPOD-42, not a project key, number, title, or URL. Use search_jira_issues to find the key or ask the user for clarification. Do not guess a project prefix.",
            };

        return await read(key);
    }
}
