using System.Net;
using System.Text;
using System.Text.Json;
using Maf.Handoff.Api;
using Microsoft.Extensions.Configuration;

namespace Maf.Handoff.Tests;

public sealed class JiraServiceTests
{
    [Fact]
    public async Task SearchUsesBasicAuthenticationAndParsesIssueFields()
    {
        RecordingHandler handler = new("""
            {"issues":[{"key":"WEPOD-42","fields":{"summary":"Deploy change","status":{"name":"Open"},"assignee":null,"created":"2026-01-01","updated":"2026-01-02"}}]}
            """);
        JiraService service = CreateService(handler);

        var issues = await service.SearchIssuesAsync("project = WEPOD", 500);

        var issue = Assert.Single(issues);
        Assert.Equal("WEPOD-42", issue.Key);
        Assert.Equal("Deploy change", issue.Summary);
        Assert.Equal("Open", issue.Status);
        Assert.Equal("Unassigned", issue.Assignee);
        Assert.Contains("maxResults=100", handler.RequestUri!.Query);
        Assert.Contains("jql=project%20%3D%20WEPOD", handler.RequestUri.Query);
        Assert.Equal("Basic", handler.AuthorizationScheme);
        Assert.Equal("test-user:test-password", Encoding.UTF8.GetString(Convert.FromBase64String(handler.AuthorizationParameter!)));
    }

    [Fact]
    public async Task TitleLookupUsesFullIssueKey()
    {
        RecordingHandler handler = new("""{"fields":{"summary":"Issue title"}}""");
        JiraService service = CreateService(handler);

        Assert.Equal("Issue title", await service.GetIssueTitleByKeyAsync("WEPOD-42"));
        Assert.EndsWith("/rest/api/2/issue/WEPOD-42?fields=summary", handler.RequestUri!.ToString());
    }

    [Fact]
    public async Task JiraErrorsDoNotExposeResponseBodies()
    {
        RecordingHandler handler = new("private remote error", HttpStatusCode.Unauthorized);
        JiraService service = CreateService(handler);

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.SearchIssuesAsync("project = WEPOD"));
        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("private remote error", error.Message);
    }

    [Fact]
    public async Task ProjectAndTextSearchUseTheReferenceServicesJqlPatterns()
    {
        RecordingHandler handler = new("""{"issues":[]}""");
        JiraService service = CreateService(handler);

        await service.GetTasksByProjectAsync("OPIM");
        Assert.Contains("project = \"OPIM\" ORDER BY updated DESC", Uri.UnescapeDataString(handler.RequestUri!.Query));

        await service.GetTasksByTextSearchAsync("payment");
        Assert.Contains("summary ~ \"payment\" OR description ~ \"payment\"", Uri.UnescapeDataString(handler.RequestUri!.Query));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetTasksByProjectAsync("OPIM OR 1=1"));
    }

    [Fact]
    public async Task SearchPassesGroupedComponentJqlThroughToJira()
    {
        RecordingHandler handler = new("""{"issues":[]}""");
        JiraService service = CreateService(handler);
        const string jql = "(component = \"Backend\" AND component = \"Infra\") OR updated >= -30d ORDER BY updated DESC";

        await service.SearchIssuesAsync(jql);

        string encodedJql = handler.RequestUri!.Query.TrimStart('?').Split('&')[0][4..];
        Assert.Equal(jql, Uri.UnescapeDataString(encodedJql));
    }

    [Fact]
    public async Task ConnectionAndDetailLookupsUseTheirJiraEndpoints()
    {
        RecordingHandler handler = new("""{"displayName":"Test User","fields":{"summary":"Example"}}""");
        JiraService service = CreateService(handler);

        Assert.Equal("Test User", (await service.GetCurrentUserAsync()).GetProperty("displayName").GetString());
        Assert.EndsWith("/rest/api/2/myself", handler.RequestUri!.ToString());

        Assert.Equal("Example", (await service.GetIssueDetailsAsync("WEPOD-42")).GetProperty("fields").GetProperty("summary").GetString());
        Assert.Contains("fields=summary,description,status,assignee,created,updated,comment", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task IntakeDetailsIncludeIntegrationFieldsAndConfiguredAcceptanceCriteria()
    {
        RecordingHandler handler = new("""
            {"key":"WEPOD-42","names":{"customfield_12345":"Acceptance Criteria"},"fields":{"description":"Expected behavior","issuelinks":[{"type":{"inward":"is blocked by"},"inwardIssue":{"key":"WEPOD-41"}}],"customfield_12345":"Existing clients still work"}}
            """);
        var details = await CreateService(handler, "customfield_12345").GetIssueDetailsAsync("WEPOD-42");

        Assert.Equal("Existing clients still work", details.GetProperty("fields").GetProperty("customfield_12345").GetString());
        Assert.Equal("WEPOD-41", details.GetProperty("fields").GetProperty("issuelinks")[0].GetProperty("inwardIssue").GetProperty("key").GetString());
        Assert.Contains("parent,subtasks,issuelinks,customfield_12345", handler.RequestUri!.Query);
        Assert.Contains("components,labels,fixVersions,versions", handler.RequestUri.Query);
        Assert.Contains("expand=names", handler.RequestUri.Query);
        Assert.Equal(HttpMethod.Get, handler.Method);
    }

    [Fact]
    public async Task CommentPagesPreserveDiscussionEvidenceAndPagination()
    {
        RecordingHandler handler = new("""
            {"startAt":50,"maxResults":100,"total":151,"comments":[{"id":"71","author":{"displayName":"Product Owner"},"created":"2026-01-01","body":"Keep the existing API compatible"}]}
            """);
        var comments = await CreateService(handler).GetIssueCommentsAsync("WEPOD-42", 50, 500);

        Assert.Equal(151, comments.GetProperty("total").GetInt32());
        Assert.Equal("71", comments.GetProperty("comments")[0].GetProperty("id").GetString());
        Assert.Equal("Keep the existing API compatible", comments.GetProperty("comments")[0].GetProperty("body").GetString());
        Assert.Contains("/rest/api/2/issue/WEPOD-42/comment?startAt=50&maxResults=100&orderBy=created", handler.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.Method);
    }

    [Fact]
    public async Task AttachmentSelectionKeepsDetailsAndCommentReadsAvailable()
    {
        RecordingHandler handler = new("""{"key":"WEPOD-42","fields":{"description":"Task context"},"total":0,"comments":[]}""");
        var selected = new SelectedJiraAttachments(CreateService(handler), "WEPOD-42", []);

        Assert.Equal("Task context", (await selected.GetIssueDetailsAsync("WEPOD-42")).GetProperty("fields").GetProperty("description").GetString());
        Assert.Equal(0, (await selected.GetIssueCommentsAsync("WEPOD-42", 3, 20)).GetProperty("total").GetInt32());
        Assert.Contains("startAt=3&maxResults=20", handler.RequestUri!.Query);
        Assert.Empty(await selected.GetAttachmentsAsync("WEPOD-42"));
        await Assert.ThrowsAsync<ArgumentException>(() => selected.ReadAttachmentAsync("WEPOD-42", "123"));
    }

    [Fact]
    public async Task IntakeReadsRejectInvalidKeysOffsetsAndCustomFieldConfigurationBeforeSending()
    {
        RecordingHandler handler = new("{}");
        JiraService service = CreateService(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetIssueDetailsAsync("../issue"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetIssueCommentsAsync("../issue"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetIssueCommentsAsync("WEPOD-42", -1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(handler, "description&expand=changelog").GetIssueDetailsAsync("WEPOD-42"));
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task CreateIssueUsesConfiguredRequesterAndAdditionalFields()
    {
        RecordingHandler handler = new("""{"key":"OPIM-123"}""");
        JiraService service = CreateService(handler);
        using var extra = System.Text.Json.JsonDocument.Parse("""{"customfield_24001":"Wepod Deployment"}""");

        string key = await service.CreateIssueAsync(new JiraCreateIssueRequest(
            "OPIM", "Application Change", "Test issue", "Test description", extra.RootElement));

        Assert.Equal("OPIM-123", key);
        Assert.Equal(HttpMethod.Post, handler.Method);
        using var body = System.Text.Json.JsonDocument.Parse(handler.RequestBody!);
        var fields = body.RootElement.GetProperty("fields");
        Assert.Equal("OPIM", fields.GetProperty("project").GetProperty("key").GetString());
        Assert.Equal("Wepod Deployment", fields.GetProperty("customfield_24001").GetString());
        Assert.Equal("test-requester", fields.GetProperty("customfield_14836").GetString());
    }

    [Fact]
    public async Task CommentPostsToTheSpecifiedIssue()
    {
        RecordingHandler handler = new("""{"id":"321"}""");
        JiraService service = CreateService(handler);

        var comment = await service.PostCommentAsync("WEPOD-42", "Ready for release");

        Assert.Equal("321", comment.GetProperty("id").GetString());
        Assert.EndsWith("/rest/api/2/issue/WEPOD-42/comment", handler.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("Ready for release", handler.RequestBody);
    }

    private static JiraService CreateService(RecordingHandler handler, string? acceptanceCriteriaField = null)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Jira:BaseUrl"] = "https://jira.example.test",
                ["Jira:User"] = "test-user",
                ["Jira:Password"] = "test-password",
                ["Jira:AuthMode"] = "Basic",
                ["Jira:Requester"] = "test-requester",
                ["Jira:AcceptanceCriteriaField"] = acceptanceCriteriaField,
            }).Build();
        return new JiraService(new HttpClient(handler), configuration);
    }

    private sealed class RecordingHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Method = request.Method;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody),
            };
        }
    }
}
