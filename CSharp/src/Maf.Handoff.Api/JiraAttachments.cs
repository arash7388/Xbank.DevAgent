using System.Text.Json;
using Maf.Handoff.Core;

namespace Maf.Handoff.Api;

public sealed partial class JiraService
{
    private const int AttachmentByteLimit = 5 * 1024 * 1024;

    public async Task<IReadOnlyList<JiraAttachmentInfo>> GetAttachmentsAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        using JsonDocument document = await GetAttachmentDocumentAsync(issueKey, cancellationToken);
        return AttachmentElements(document).Select(item => new JiraAttachmentInfo(
            ReadString(item, "id"), ReadString(item, "filename"), ReadString(item, "mimeType"),
            item.TryGetProperty("size", out JsonElement size) ? size.GetInt64() : 0)).ToArray();
    }

    public async Task<JiraAttachmentText> ReadAttachmentAsync(string issueKey, string attachmentId, int offset = 0, CancellationToken cancellationToken = default)
    {
        if (offset < 0) throw new ArgumentException("Offset cannot be negative.");
        using JsonDocument document = await GetAttachmentDocumentAsync(issueKey, cancellationToken);
        JsonElement attachment = AttachmentElements(document).FirstOrDefault(item => ReadString(item, "id") == attachmentId);
        if (attachment.ValueKind != JsonValueKind.Object) throw new ArgumentException("Attachment does not belong to this Jira issue.");
        string fileName = ReadString(attachment, "filename");
        long size = attachment.GetProperty("size").GetInt64();
        using MemoryStream data = new();
        FormFile file = new(data, 0, size, "attachment", fileName);
        ReferenceDocumentService.ValidateFile(file);
        Uri baseUrl = GetBaseUrl();
        if (!Uri.TryCreate(ReadString(attachment, "content"), UriKind.Absolute, out Uri? contentUrl) ||
            contentUrl.Scheme != baseUrl.Scheme || contentUrl.Authority != baseUrl.Authority || !string.IsNullOrEmpty(contentUrl.UserInfo))
            throw new ArgumentException("Attachment URL must use the configured Jira server.");
        using HttpRequestMessage request = new(HttpMethod.Get, contentUrl);
        AddAuthentication(request);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > AttachmentByteLimit) throw new ArgumentException("Attachment exceeds the 5 MB limit.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (data.Length + count > AttachmentByteLimit) throw new ArgumentException("Attachment exceeds the 5 MB limit.");
            await data.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        data.Position = 0;
        string text = await ReferenceDocumentService.ExtractTextAsync(new FormFile(data, 0, data.Length, "attachment", fileName), cancellationToken);
        if (offset > text.Length) throw new ArgumentException("Offset exceeds the attachment text length.");
        string excerpt = text.Substring(offset, Math.Min(12000, text.Length - offset));
        return new(issueKey, attachmentId, fileName, excerpt, offset, text.Length, offset + excerpt.Length < text.Length, text.Length >= 400000);
    }

    private Task<JsonDocument> GetAttachmentDocumentAsync(string issueKey, CancellationToken cancellationToken)
    {
        ValidateIssueKey(issueKey);
        return GetJsonAsync($"rest/api/2/issue/{Uri.EscapeDataString(issueKey)}?fields=attachment", cancellationToken);
    }

    private static IEnumerable<JsonElement> AttachmentElements(JsonDocument document) =>
        document.RootElement.TryGetProperty("fields", out JsonElement fields) &&
        fields.TryGetProperty("attachment", out JsonElement attachments) && attachments.ValueKind == JsonValueKind.Array
            ? attachments.EnumerateArray().ToArray() : [];
}
