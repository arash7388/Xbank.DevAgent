using System.Net;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xbank.DevAgent.Api;
using Microsoft.Extensions.Configuration;

namespace Xbank.DevAgent.Tests;

public sealed class JiraAttachmentTests
{
    [Fact]
    public async Task AcceptsEpfsBinaryAttachmentWithExplicitExtractionStatus()
    {
        byte[] bytes = [0x45, 0x50, 0x46, 0x53, 0, 0, 0, 0x0F, 2, 0, 0, 0, 1, 0, 0, 0, 0x93, 0x75, 0x9E];
        var jira = Create(new Handler(bytes, "ggg.txt"));
        var result = await jira.ReadAttachmentAsync("TEST-1", "123");
        Assert.Equal("epfs-binary", result.ContentFormat);
        Assert.Equal(bytes.Length, result.SourceBytes);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), result.ContentSha256);
        Assert.Empty(result.Text);
        Assert.Equal(0, result.TotalCharacters);
        Assert.False(result.HasMore);
        Assert.False(result.ExtractionLimitReached);
        Assert.Contains("No document text was extracted", result.ExtractionNotice);

        var selected = new SelectedJiraAttachments(jira, "TEST-1", ["123"]);
        string context = await selected.LoadContextAsync();
        Assert.Contains("Filename: ggg.txt", context);
        Assert.Contains("epfs-binary", context);
        Assert.Contains("Attachment accepted", context);
        Assert.DoesNotContain("Could not load", context);
        await Assert.ThrowsAsync<ArgumentException>(() => jira.ReadAttachmentAsync("TEST-1", "123", 1));
    }

    [Fact]
    public async Task TextBeginningWithEpfsIsStillReadAsText()
    {
        const string text = "EPFS is mentioned in this clarification.";
        var result = await Create(new Handler(Encoding.UTF8.GetBytes(text), "ggg.txt")).ReadAttachmentAsync("TEST-1", "123");
        Assert.Equal("text", result.ContentFormat);
        Assert.Equal(text, result.Text);
        Assert.Null(result.ExtractionNotice);
    }

    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("utf-8", true)]
    [InlineData("utf-16", false)]
    [InlineData("utf-16", true)]
    [InlineData("utf-16BE", false)]
    [InlineData("utf-16BE", true)]
    [InlineData("utf-32", false)]
    [InlineData("utf-32", true)]
    [InlineData("utf-32BE", false)]
    [InlineData("utf-32BE", true)]
    public async Task ReadsUnicodeAttachmentIntoToolsAndAutomaticContext(string encodingName, bool includeBom)
    {
        const string text = "Clarification: توضیحات فارسی\r\nUnicode requirements: café, 日本語, 😀";
        Encoding encoding = Encoding.GetEncoding(encodingName);
        byte[] bytes = includeBom ? [.. encoding.GetPreamble(), .. encoding.GetBytes(text)] : encoding.GetBytes(text);
        var jira = Create(new Handler(bytes, "ggg.txt"));
        var result = await jira.ReadAttachmentAsync("TEST-1", "123");
        Assert.Equal(text, result.Text);
        Assert.Equal(text.Length, result.TotalCharacters);
        Assert.False(result.HasMore);

        var selected = new SelectedJiraAttachments(jira, "TEST-1", ["123"]);
        Assert.Contains(text, await selected.LoadContextAsync());
    }

    [Fact]
    public async Task PagesUnicodeAttachmentByDecodedCharacters()
    {
        string text = new string('x', 11999) + "توضیحات فارسی";
        var jira = Create(new Handler(Encoding.Unicode.GetBytes(text), "ggg.txt"));
        var first = await jira.ReadAttachmentAsync("TEST-1", "123");
        var second = await jira.ReadAttachmentAsync("TEST-1", "123", 12000);
        Assert.Equal(text, first.Text + second.Text);
        Assert.Equal(text.Length, first.TotalCharacters);
        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Equal(first.ContentSha256, second.ContentSha256);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.Unicode.GetBytes(text))).ToLowerInvariant(), first.ContentSha256);
    }

    [Theory]
    [InlineData(new byte[] { 0xC3, 0x28 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x41 })]
    public async Task ReportsInvalidEncodingInsteadOfSilentlyReplacingCharacters(byte[] bytes)
    {
        var jira = Create(new Handler(bytes, "ggg.txt"));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => jira.ReadAttachmentAsync("TEST-1", "123"));
        Assert.Contains("encoding", error.Message);
        var selected = new SelectedJiraAttachments(jira, "TEST-1", ["123"]);
        Assert.Contains("invalid or unrecognized encoding", await selected.LoadContextAsync());
    }

    [Fact]
    public async Task AutomaticContextLimitLeavesRemainingSelectedFilesAvailableToTools()
    {
        Handler handler = new(Encoding.UTF8.GetBytes(new string('x', 13000)), "requirements.txt", attachmentCount: 5);
        var selected = new SelectedJiraAttachments(Create(handler), "TEST-1", ["123", "124", "125", "126", "127"]);
        string context = await selected.LoadContextAsync();
        Assert.Equal(48000, context.Split('\n').Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0 && line.All(character => character == 'x')).Sum(line => line.Length));
        Assert.Contains("Attachment TEST-1/127:", context);
        Assert.Contains("automatic context limit reached", context);
        Assert.Equal(4, handler.Downloads);
        Assert.Equal(12000, (await selected.ReadAttachmentAsync("TEST-1", "127")).Text.Length);
        Assert.Equal(5, handler.Downloads);
    }

    [Fact]
    public async Task AutomaticallyLoadsSelectedAttachmentTextAndReportsPaging()
    {
        Handler handler = new(Encoding.UTF8.GetBytes(new string('x', 13000)), "requirements.txt");
        var selected = new SelectedJiraAttachments(Create(handler), "TEST-1", ["123"]);
        string context = await selected.LoadContextAsync();
        Assert.Contains("Filename: requirements.txt", context);
        Assert.Contains(new string('x', 12000), context);
        Assert.Contains("offset 12000", context);
        Assert.Equal(1, handler.Downloads);
        Assert.Equal(1000, (await selected.ReadAttachmentAsync("TEST-1", "123", 12000)).Text.Length);
    }

    [Fact]
    public async Task ExcludingAttachmentsDoesNotDownloadAndBlocksAgentAccess()
    {
        Handler handler = new(Encoding.UTF8.GetBytes("Private requirements"), "requirements.txt");
        var selected = new SelectedJiraAttachments(Create(handler), "TEST-1", []);
        Assert.Contains("excluded all attachments", await selected.LoadContextAsync());
        Assert.Empty(await selected.GetAttachmentsAsync("TEST-1"));
        var excluded = await selected.ReadAttachmentAsync("TEST-1", "123");
        Assert.Equal("excluded-by-user", excluded.ContentFormat);
        Assert.Empty(excluded.Text);
        Assert.False(excluded.HasMore);
        Assert.Contains("Do not retry", excluded.ExtractionNotice);
        Assert.Empty((await selected.GetIssueDetailsAsync("TEST-1")).GetProperty("fields").GetProperty("attachment").EnumerateArray());
        Assert.Equal(0, handler.Downloads);
    }

    [Fact]
    public async Task SelectionFiltersListingAndBlocksOtherFilesAndIssues()
    {
        Handler handler = new(Encoding.UTF8.GetBytes("requirements"), "requirements.txt");
        var selected = new SelectedJiraAttachments(Create(handler), "TEST-1", ["123"]);
        Assert.Single(await selected.GetAttachmentsAsync("test-1"));
        Assert.Empty(await selected.GetAttachmentsAsync("TEST-2"));
        Assert.Equal("excluded-by-user", (await selected.ReadAttachmentAsync("TEST-1", "999")).ContentFormat);
        Assert.Equal("excluded-by-user", (await selected.ReadAttachmentAsync("TEST-2", "123")).ContentFormat);
        Assert.Equal(0, handler.Downloads);
    }

    [Fact]
    public async Task SelectionFiltersIssueDetailsAndHonorsCancellationForExcludedReads()
    {
        Handler handler = new(Encoding.UTF8.GetBytes("requirements"), "requirements.txt", attachmentCount: 2);
        var selected = new SelectedJiraAttachments(Create(handler), "TEST-1", ["123"]);
        var attachments = (await selected.GetIssueDetailsAsync("test-1")).GetProperty("fields").GetProperty("attachment");
        Assert.Equal("123", Assert.Single(attachments.EnumerateArray()).GetProperty("id").GetString());
        Assert.Empty((await selected.GetIssueDetailsAsync("TEST-2")).GetProperty("fields").GetProperty("attachment").EnumerateArray());
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selected.ReadAttachmentAsync("TEST-1", "999", cancellationToken: cancellation.Token));
        Assert.Equal(0, handler.Downloads);
    }

    [Fact]
    public async Task AutomaticLoadingReportsUnsupportedAttachmentsWithoutFailingWorkflow()
    {
        Handler handler = new(Encoding.UTF8.GetBytes("image data"), "image.png");
        var selected = new SelectedJiraAttachments(Create(handler), "TEST-1", ["123"]);
        Assert.Contains("Could not load this attachment", await selected.LoadContextAsync());
        Assert.Equal(0, handler.Downloads);
    }

    [Fact]
    public async Task AutomaticLoadingHonorsCancellation()
    {
        Handler handler = new(Encoding.UTF8.GetBytes("requirements"), "requirements.txt");
        var selected = new SelectedJiraAttachments(Create(handler), "TEST-1", ["123"]);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selected.LoadContextAsync(cancellation.Token));
        Assert.Equal(0, handler.Downloads);
    }

    [Fact]
    public async Task ListsAttachmentsAndReadsAuthenticatedTextInPages()
    {
        Handler handler = new(Encoding.UTF8.GetBytes(new string('x', 13000)), "requirements.txt");
        var jira = Create(handler);
        var info = Assert.Single(await jira.GetAttachmentsAsync("TEST-1"));
        Assert.Equal("123", info.Id);
        var first = await jira.ReadAttachmentAsync("TEST-1", "123");
        Assert.Equal(12000, first.Text.Length);
        Assert.True(first.HasMore);
        var last = await jira.ReadAttachmentAsync("TEST-1", "123", 12000);
        Assert.Equal(1000, last.Text.Length);
        Assert.False(last.HasMore);
        Assert.Equal("Basic", handler.DownloadAuthentication);
    }

    [Theory]
    [InlineData("123", "requirements.txt", "https://other.example/file")]
    [InlineData("999", "requirements.txt", "https://jira.example.test/file")]
    [InlineData("123", "image.png", "https://jira.example.test/file")]
    public async Task RejectsForeignUrlsUnknownIdsAndUnsupportedFiles(string id, string filename, string url)
    {
        Handler handler = new(Encoding.UTF8.GetBytes("requirements"), filename, url);
        await Assert.ThrowsAsync<ArgumentException>(() => Create(handler).ReadAttachmentAsync("TEST-1", id));
        Assert.Equal(0, handler.Downloads);
    }

    [Fact]
    public async Task ReadsWordDocumentText()
    {
        using MemoryStream stream = new();
        using (var word = WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document, true))
        {
            var main = word.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new Run(new Text("Word requirements")))));
            main.Document.Save();
        }
        var result = await Create(new Handler(stream.ToArray(), "requirements.docx")).ReadAttachmentAsync("TEST-1", "123");
        Assert.Contains("Word requirements", result.Text);
    }

    [Fact]
    public async Task ReadsPdfText()
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("PDF requirements", 12, new UglyToad.PdfPig.Core.PdfPoint(20, 100), font);
        var result = await Create(new Handler(builder.Build(), "requirements.pdf")).ReadAttachmentAsync("TEST-1", "123");
        Assert.Contains("PDF requirements", result.Text);
    }

    [Fact]
    public async Task RejectsOversizedDownloadsEvenWhenMetadataUnderstatesSize()
    {
        Handler handler = new(new byte[5 * 1024 * 1024 + 1], "requirements.txt", reportedSize: 20);
        await Assert.ThrowsAsync<ArgumentException>(() => Create(handler).ReadAttachmentAsync("TEST-1", "123"));
    }

    private static JiraService Create(Handler handler) => new(new HttpClient(handler),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jira:BaseUrl"] = "https://jira.example.test", ["Jira:User"] = "test", ["Jira:Password"] = "secret",
        }).Build());

    private sealed class Handler(byte[] bytes, string filename, string url = "https://jira.example.test/file", long? reportedSize = null, int attachmentCount = 1) : HttpMessageHandler
    {
        public int Downloads { get; private set; }
        public string? DownloadAuthentication { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/issue/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    fields = new { attachment = Enumerable.Range(123, attachmentCount).Select(id => new { id = id.ToString(), filename, mimeType = "text/plain", size = reportedSize ?? bytes.Length, content = url }).ToArray() },
                })) });
            Downloads++;
            DownloadAuthentication = request.Headers.Authorization?.Scheme;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
