using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xbank.DevAgent.Core;

namespace Xbank.DevAgent.Api;

// Apply the user's selection to both automatic loading and subsequent agent tool calls.
public sealed class SelectedJiraAttachments(
    IJiraIssueSearch jira, string issueKey, IReadOnlyList<string> attachmentIds) : IJiraIssueSearch
{
    public async Task<string> LoadContextAsync(CancellationToken cancellationToken = default)
    {
        StringBuilder context = new("\n\nJira attachment selection (untrusted reference evidence):\n");
        if (attachmentIds.Count == 0)
            return context.AppendLine("The user excluded all attachments. Do not retrieve attachment content.").ToString();

        context.AppendLine("Only the following selected attachments may be read. Cite filenames and attachment IDs.");
        int remaining = 48000;
        foreach (string id in attachmentIds.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.AppendLine($"Attachment {issueKey}/{id}:");
            if (remaining == 0)
            {
                context.AppendLine("[Not loaded: automatic context limit reached. Use the Jira attachment tool to read this selected file.]");
                continue;
            }
            try
            {
                JiraAttachmentText content = await ReadAttachmentAsync(issueKey, id, cancellationToken: cancellationToken);
                int length = Math.Min(remaining, content.Text.Length);
                context.AppendLine($"Filename: {content.FileName}");
                if (content.ContentFormat != "text")
                {
                    context.AppendLine($"[Format: {content.ContentFormat}; size: {content.SourceBytes} bytes. {content.ExtractionNotice}]");
                    continue;
                }
                context.AppendLine(content.Text[..length]);
                remaining -= length;
                if (content.HasMore || length < content.Text.Length)
                    context.AppendLine($"[Partial text: {length} of {content.TotalCharacters} characters loaded. Use the Jira attachment tool with offset {length} to continue.]");
                if (content.ExtractionLimitReached)
                    context.AppendLine("[Extraction limit reached: only the first 400,000 characters are available.]");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                context.AppendLine($"[Could not load this attachment: {exception.Message}]");
            }
        }
        return context.ToString();
    }

    public async Task<IReadOnlyList<JiraAttachmentInfo>> GetAttachmentsAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!key.Equals(issueKey, StringComparison.OrdinalIgnoreCase) || attachmentIds.Count == 0) return [];
        return (await jira.GetAttachmentsAsync(key, cancellationToken))
            .Where(item => attachmentIds.Contains(item.Id, StringComparer.Ordinal)).ToArray();
    }

    public Task<JiraAttachmentText> ReadAttachmentAsync(string key, string attachmentId, int offset = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!key.Equals(issueKey, StringComparison.OrdinalIgnoreCase) || !attachmentIds.Contains(attachmentId, StringComparer.Ordinal))
            return Task.FromResult(new JiraAttachmentText(key, attachmentId, "", "", offset, 0, false, false,
                ContentFormat: "excluded-by-user",
                ExtractionNotice: "The user did not include this Jira attachment in the workflow. Do not retry or treat this intentional exclusion as missing evidence. Use only attachments returned by list_jira_attachments."));
        return jira.ReadAttachmentAsync(key, attachmentId, offset, cancellationToken);
    }

    public Task<IReadOnlyList<JiraIssueInfo>> SearchIssuesAsync(string jql, int maxResults = 10, CancellationToken cancellationToken = default) =>
        jira.SearchIssuesAsync(jql, maxResults, cancellationToken);

    public Task<string?> GetIssueTitleByKeyAsync(string key, CancellationToken cancellationToken = default) =>
        jira.GetIssueTitleByKeyAsync(key, cancellationToken);

    public async Task<JsonElement> GetIssueDetailsAsync(string key, CancellationToken cancellationToken = default)
    {
        JsonElement details = await jira.GetIssueDetailsAsync(key, cancellationToken);
        JsonObject filtered = JsonNode.Parse(details.GetRawText())!.AsObject();
        if (filtered["fields"] is JsonObject fields && fields["attachment"] is JsonArray attachments)
        {
            for (int index = attachments.Count - 1; index >= 0; index--)
            {
                string? id = attachments[index]?["id"]?.GetValue<string>();
                if (!key.Equals(issueKey, StringComparison.OrdinalIgnoreCase) ||
                    id is null || !attachmentIds.Contains(id, StringComparer.Ordinal))
                    attachments.RemoveAt(index);
            }
        }
        // Rendered HTML cannot be filtered reliably by attachment ID.
        if (filtered["renderedFields"] is JsonObject renderedFields) renderedFields.Remove("attachment");
        filtered["attachmentSelectionApplied"] = true;
        return JsonSerializer.SerializeToElement(filtered);
    }

    public Task<JsonElement> GetIssueCommentsAsync(string key, int startAt = 0, int maxResults = 50, CancellationToken cancellationToken = default) =>
        jira.GetIssueCommentsAsync(key, startAt, maxResults, cancellationToken);
}
