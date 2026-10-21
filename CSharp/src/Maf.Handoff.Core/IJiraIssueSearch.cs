using System.Text.Json;

namespace Maf.Handoff.Core;

public sealed record JiraIssueInfo(
    string Key,
    string Summary,
    string Status,
    string Assignee,
    string Created,
    string Updated);

public sealed record JiraAttachmentInfo(string Id, string FileName, string MimeType, long Size);
public sealed record JiraAttachmentText(string IssueKey, string AttachmentId, string FileName, string Text, int Offset, int TotalCharacters, bool HasMore, bool ExtractionLimitReached,
    string ContentFormat = "text", string? ExtractionNotice = null, long? SourceBytes = null, string? ContentSha256 = null);

public interface IJiraIssueSearch
{
    Task<JsonElement> GetIssueDetailsAsync(string issueKey, CancellationToken cancellationToken = default);
    Task<JsonElement> GetIssueCommentsAsync(string issueKey, int startAt = 0, int maxResults = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JiraAttachmentInfo>> GetAttachmentsAsync(string issueKey, CancellationToken cancellationToken = default);
    Task<JiraAttachmentText> ReadAttachmentAsync(string issueKey, string attachmentId, int offset = 0, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JiraIssueInfo>> SearchIssuesAsync(
        string jql,
        int maxResults = 10,
        CancellationToken cancellationToken = default);

    Task<string?> GetIssueTitleByKeyAsync(
        string issueKey,
        CancellationToken cancellationToken = default);
}
