using System.Net;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Maf.Handoff.Api;
using Microsoft.Extensions.Configuration;

namespace Maf.Handoff.Tests;

public sealed class JiraAttachmentTests
{
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
        await Assert.ThrowsAsync<ArgumentException>(() => selected.ReadAttachmentAsync("TEST-1", "123"));
        Assert.Equal(0, handler.Downloads);
    }

    [Fact]
    public async Task SelectionFiltersListingAndBlocksOtherFilesAndIssues()
    {
        Handler handler = new(Encoding.UTF8.GetBytes("requirements"), "requirements.txt");
        var selected = new SelectedJiraAttachments(Create(handler), "TEST-1", ["123"]);
        Assert.Single(await selected.GetAttachmentsAsync("test-1"));
        Assert.Empty(await selected.GetAttachmentsAsync("TEST-2"));
        await Assert.ThrowsAsync<ArgumentException>(() => selected.ReadAttachmentAsync("TEST-1", "999"));
        await Assert.ThrowsAsync<ArgumentException>(() => selected.ReadAttachmentAsync("TEST-2", "123"));
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
