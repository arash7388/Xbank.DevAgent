using System.Text.Json;
using System.Text.Json.Nodes;
using Maf.Handoff.Core;

namespace Maf.Handoff.Api;

// Enforce the run's comment selection for issue details and paginated tool reads.
public sealed class SelectedJiraComments(IJiraIssueSearch jira, bool includeComments) : IJiraIssueSearch
{
    public async Task<JsonElement> GetIssueDetailsAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        JsonElement details = await jira.GetIssueDetailsAsync(issueKey, cancellationToken);
        if (includeComments) return details;

        JsonObject filtered = JsonNode.Parse(details.GetRawText())!.AsObject();
        if (filtered["fields"] is JsonObject fields) fields.Remove("comment");
        if (filtered["renderedFields"] is JsonObject renderedFields) renderedFields.Remove("comment");
        filtered["commentsExcludedByUser"] = true;
        return JsonSerializer.SerializeToElement(filtered);
    }

    public Task<JsonElement> GetIssueCommentsAsync(string issueKey, int startAt = 0, int maxResults = 50,
        CancellationToken cancellationToken = default) => includeComments
        ? jira.GetIssueCommentsAsync(issueKey, startAt, maxResults, cancellationToken)
        : Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            startAt, maxResults, total = 0, comments = Array.Empty<object>(), commentsExcludedByUser = true,
        }));

    public Task<IReadOnlyList<JiraIssueInfo>> SearchIssuesAsync(string jql, int maxResults = 10,
        CancellationToken cancellationToken = default) => jira.SearchIssuesAsync(jql, maxResults, cancellationToken);

    public Task<string?> GetIssueTitleByKeyAsync(string issueKey, CancellationToken cancellationToken = default) =>
        jira.GetIssueTitleByKeyAsync(issueKey, cancellationToken);

    public Task<IReadOnlyList<JiraAttachmentInfo>> GetAttachmentsAsync(string issueKey,
        CancellationToken cancellationToken = default) => jira.GetAttachmentsAsync(issueKey, cancellationToken);

    public Task<JiraAttachmentText> ReadAttachmentAsync(string issueKey, string attachmentId, int offset = 0,
        CancellationToken cancellationToken = default) => jira.ReadAttachmentAsync(issueKey, attachmentId, offset, cancellationToken);
}
