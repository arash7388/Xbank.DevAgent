using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Maf.Handoff.Core;

internal sealed record JiraIntakeFingerprint(
    string IssueKey, string TitleHash, string DescriptionHash, string CommentsHash,
    string AttachmentsHash, string EvidenceHash)
{
    internal static string Hash(object? value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        CheckpointSerialization.Canonicalize(JsonSerializer.SerializeToElement(value, JsonOptions))))).ToLowerInvariant();

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<JiraIntakeFingerprint?> ReadAsync(
        IJiraIssueSearch jira, string key, CancellationToken cancellationToken)
    {
        JsonElement details = await jira.GetIssueDetailsAsync(key, cancellationToken);
        if (!details.TryGetProperty("fields", out JsonElement fields) ||
            !fields.TryGetProperty("summary", out JsonElement title) ||
            !fields.TryGetProperty("description", out JsonElement description)) return null;

        List<JsonElement> comments = [];
        int offset = 0;
        int? expectedTotal = null;
        // Bound the optimization; incomplete or changing pagination must never produce a cache hit.
        for (int pageNumber = 0; ; pageNumber++)
        {
            if (pageNumber >= 100) return null;
            JsonElement page = await jira.GetIssueCommentsAsync(key, offset, 100, cancellationToken);
            if (!page.TryGetProperty("total", out JsonElement totalValue) || !totalValue.TryGetInt32(out int total) ||
                total < 0 || !page.TryGetProperty("startAt", out JsonElement startValue) ||
                !startValue.TryGetInt32(out int start) || start != offset ||
                !page.TryGetProperty("comments", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
                return null;
            if (expectedTotal is not null && expectedTotal != total) return null;
            expectedTotal = total;
            comments.AddRange(items.EnumerateArray().Select(item => item.Clone()));
            offset += items.GetArrayLength();
            if (offset == total) break;
            if (offset > total || items.GetArrayLength() == 0) return null;
        }

        List<object> attachments = [];
        foreach (JiraAttachmentInfo attachment in (await jira.GetAttachmentsAsync(key, cancellationToken))
            .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            JiraAttachmentText content = await jira.ReadAttachmentAsync(key, attachment.Id, cancellationToken: cancellationToken);
            string? hash = content.ContentSha256;
            if (string.IsNullOrEmpty(hash))
            {
                // Other implementations can supply complete plain text, but partial/binary content needs a source hash.
                if (content.ContentFormat != "text" || content.HasMore || content.ExtractionLimitReached ||
                    content.Offset != 0 || content.Text.Length != content.TotalCharacters) return null;
                hash = Hash(content.Text);
            }
            attachments.Add(new { attachment.Id, attachment.FileName, attachment.MimeType, attachment.Size, hash });
        }

        return new(key.ToUpperInvariant(), Hash(title), Hash(description), Hash(comments), Hash(attachments),
            Hash(new { details, comments, attachments }));
    }
}

internal sealed record JiraIntakeRead(string Name, Dictionary<string, JsonElement> Arguments, string ResultHash);
internal sealed record JiraIntakeCheckpointEntry(string ResponseJson, IReadOnlyList<JiraIntakeRead> Reads);

internal interface IJiraIntakeCheckpointStore
{
    Task<JiraIntakeCheckpointEntry?> FindJiraIntakeAsync(string cacheKey, CancellationToken cancellationToken);
    Task SaveJiraIntakeAsync(string cacheKey, JiraIntakeFingerprint fingerprint, string workflowId,
        JiraIntakeCheckpointEntry entry, CancellationToken cancellationToken);
}

public sealed partial class SqliteLlmCheckpointStore : IJiraIntakeCheckpointStore
{
    private static void InitializeJiraIntakeSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS jira_intake_checkpoints (
                cache_key TEXT PRIMARY KEY, issue_key TEXT NOT NULL,
                title_hash TEXT NOT NULL, description_hash TEXT NOT NULL,
                comments_hash TEXT NOT NULL, attachments_hash TEXT NOT NULL,
                evidence_hash TEXT NOT NULL, workflow_id TEXT NOT NULL,
                response_json TEXT NOT NULL, reads_json TEXT NOT NULL, completed_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_jira_intake_issue
            ON jira_intake_checkpoints(issue_key, completed_at DESC);
            """;
        command.ExecuteNonQuery();
    }

    async Task<JiraIntakeCheckpointEntry?> IJiraIntakeCheckpointStore.FindJiraIntakeAsync(
        string cacheKey, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT response_json, reads_json FROM jira_intake_checkpoints WHERE cache_key = $key";
        command.Parameters.AddWithValue("$key", cacheKey);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), JsonSerializer.Deserialize<JiraIntakeRead[]>(reader.GetString(1),
                JiraIntakeFingerprint.JsonOptions)!) : null;
    }

    async Task IJiraIntakeCheckpointStore.SaveJiraIntakeAsync(string cacheKey, JiraIntakeFingerprint fingerprint,
        string workflowId, JiraIntakeCheckpointEntry entry, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jira_intake_checkpoints
                (cache_key, issue_key, title_hash, description_hash, comments_hash, attachments_hash,
                 evidence_hash, workflow_id, response_json, reads_json, completed_at)
            VALUES ($key, $issue, $title, $description, $comments, $attachments, $evidence, $workflow, $response, $reads, $now)
            ON CONFLICT(cache_key) DO UPDATE SET workflow_id = excluded.workflow_id,
                response_json = excluded.response_json, reads_json = excluded.reads_json, completed_at = excluded.completed_at;
            """;
        command.Parameters.AddWithValue("$key", cacheKey);
        command.Parameters.AddWithValue("$issue", fingerprint.IssueKey);
        command.Parameters.AddWithValue("$title", fingerprint.TitleHash);
        command.Parameters.AddWithValue("$description", fingerprint.DescriptionHash);
        command.Parameters.AddWithValue("$comments", fingerprint.CommentsHash);
        command.Parameters.AddWithValue("$attachments", fingerprint.AttachmentsHash);
        command.Parameters.AddWithValue("$evidence", fingerprint.EvidenceHash);
        command.Parameters.AddWithValue("$workflow", workflowId);
        command.Parameters.AddWithValue("$response", entry.ResponseJson);
        command.Parameters.AddWithValue("$reads", JsonSerializer.Serialize(entry.Reads, JiraIntakeFingerprint.JsonOptions));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
