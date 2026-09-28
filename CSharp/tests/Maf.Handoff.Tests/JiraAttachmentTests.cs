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

    private sealed class Handler(byte[] bytes, string filename, string url = "https://jira.example.test/file", long? reportedSize = null) : HttpMessageHandler
    {
        public int Downloads { get; private set; }
        public string? DownloadAuthentication { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/issue/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    fields = new { attachment = new[] { new { id = "123", filename, mimeType = "text/plain", size = reportedSize ?? bytes.Length, content = url } } },
                })) });
            Downloads++;
            DownloadAuthentication = request.Headers.Authorization?.Scheme;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
