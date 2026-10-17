using System.Net;
using System.Text.Json;
using Maf.Handoff.Api;
using Maf.Handoff.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace Maf.Handoff.Tests;

public sealed class WorkflowJiraContextTests
{
    private const string Problem = "درخواست مبهم است؛ مقدار مورد انتظار RetryLimit مشخص نیست. لطفاً مقدار صحیح و معیار پذیرش را اعلام کنید.";

    [Fact]
    public async Task PostsPersianProblemOnlyToBoundTaskAndPublishesActualResult()
    {
        RecordingHandler handler = new();
        List<RunEvent> events = [];
        WorkflowJiraContext context = Create(handler, "WEPOD-42", events.Add);

        JiraProblemReport report = await context.ReportProblemAsync(Problem);

        Assert.Equal("posted", report.Status);
        Assert.Equal("321", report.CommentId);
        Assert.Equal("https://jira.example.test/jira/browse/WEPOD-42?focusedCommentId=321", report.Url);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.EndsWith("/jira/rest/api/2/issue/WEPOD-42/comment", handler.Url);
        using JsonDocument json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("گزارش بررسی آمادگی تسک توسط عامل Jira\n\n" + Problem, json.RootElement.GetProperty("body").GetString());
        RunEvent notice = Assert.Single(events);
        Assert.Equal("jira_problem", notice.Type);
        Assert.Equal("posted", notice.Status);
        Assert.Equal("WEPOD-42", notice.Source);
        Assert.Contains(Problem, notice.Text);
    }

    [Fact]
    public async Task ConcurrentAndRepeatedReportsPostOnlyOneConsolidatedComment()
    {
        RecordingHandler handler = new();
        List<RunEvent> events = [];
        WorkflowJiraContext context = Create(handler, "WEPOD-42", events.Add);

        JiraProblemReport[] reports = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => context.ReportProblemAsync(Problem)));

        Assert.Equal(1, handler.Calls);
        Assert.Single(events);
        Assert.All(reports, report => Assert.Equal(reports[0], report));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrUncertainPostsRemainVisibleAndAreNotRetried(bool networkFailure)
    {
        RecordingHandler handler = new(HttpStatusCode.Forbidden, networkFailure);
        List<RunEvent> events = [];
        WorkflowJiraContext context = Create(handler, "WEPOD-42", events.Add);

        JiraProblemReport result = await context.ReportProblemAsync(Problem);
        Assert.Equal(result, await context.ReportProblemAsync(Problem));

        Assert.Equal("failed", result.Status);
        Assert.Contains(Problem, result.Message);
        Assert.DoesNotContain("private remote error", result.Message);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("failed", Assert.Single(events).Status);
    }

    [Fact]
    public async Task MissingTaskAndNonPersianTextNeverCauseAWrite()
    {
        RecordingHandler handler = new();
        List<RunEvent> events = [];
        WorkflowJiraContext context = Create(handler, null, events.Add);
        Assert.Equal("not_posted", (await context.ReportProblemAsync(Problem)).Status);
        Assert.Equal(0, handler.Calls);

        context = Create(handler, "WEPOD-42", events.Add);
        Assert.Equal("failed", (await context.ReportProblemAsync("The task is unclear.")).Status);
        Assert.Equal(0, handler.Calls);
        Assert.Equal("posted", (await context.ReportProblemAsync(Problem)).Status);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AcceptedPostIsNotRepeatedIfLocalNotificationFails()
    {
        RecordingHandler handler = new();
        WorkflowJiraContext context = Create(handler, "WEPOD-42", _ => throw new InvalidOperationException("Persistence unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.ReportProblemAsync(Problem));
        Assert.Equal("posted", (await context.ReportProblemAsync(Problem)).Status);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("WEPOD-42", "Related OTHER-1 and OTHER-2", "WEPOD-42")]
    [InlineData(null, "Fix WEPOD-42", "WEPOD-42")]
    [InlineData(null, "Fix WEPOD-42; see wepod-42", "WEPOD-42")]
    [InlineData(null, "Compare WEPOD-42 and WEPOD-43", null)]
    [InlineData(null, "Fix task 42", null)]
    public void ResolvesOnlyAnUnambiguousRelatedTask(string? selected, string task, string? expected) =>
        Assert.Equal(expected, WorkflowJiraContext.ResolveIssueKey(selected, task));

    private static WorkflowJiraContext Create(RecordingHandler handler, string? key, Action<RunEvent> notify)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jira:BaseUrl"] = "https://jira.example.test/jira/", ["Jira:User"] = "test-user", ["Jira:Password"] = "test-password",
        }).Build();
        JiraService jira = new(new HttpClient(handler), config);
        return new(jira, jira, key, notify);
    }

    [Fact]
    public async Task AdditionalChangesGetASeparateCommentAndIdenticalReportsAreDeduplicated()
    {
        RecordingHandler handler = new();
        List<RunEvent> events = [];
        WorkflowJiraContext context = Create(handler, "WEPOD-42", events.Add);
        await context.ReportProblemAsync(Problem);
        string changes = "تغییر خارج از شرح تسک: اصلاح اعتبارسنجی در Settings.cs برای جلوگیری از مقدار منفی؛ آزمون مرزی موفق بود.";
        JiraProblemReport report = await context.ReportScopeChangeAsync(changes);
        Assert.Equal(report, await context.ReportScopeChangeAsync(changes));
        Assert.Equal("posted", report.Status);
        Assert.Equal(2, handler.Calls);
        Assert.Equal("jira_problem", events[0].Type);
        Assert.Equal("jira_scope_change", events[1].Type);
        Assert.Equal("WEPOD-42", events[1].Source);
        using JsonDocument json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("گزارش تغییرات خارج از شرح تسک Jira\n\n" + changes, json.RootElement.GetProperty("body").GetString());
        await context.ReportScopeChangeAsync("اصلاح تکمیلی در Validator.cs برای رفع خطای بازبینی؛ آزمون موفق بود.");
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedScopeCommentsAreVisibleAndNotRetried(bool networkFailure)
    {
        RecordingHandler handler = new(HttpStatusCode.Forbidden, networkFailure);
        List<RunEvent> events = [];
        WorkflowJiraContext context = Create(handler, "WEPOD-42", events.Add);
        JiraProblemReport report = await context.ReportScopeChangeAsync(Problem);
        Assert.Equal(report, await context.ReportScopeChangeAsync(Problem));
        Assert.Equal("failed", report.Status);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("jira_scope_change", Assert.Single(events).Type);
    }

    [Fact]
    public async Task ScopeChangesWithoutRelatedTaskNeverWriteToJira()
    {
        RecordingHandler handler = new();
        List<RunEvent> events = [];
        WorkflowJiraContext context = Create(handler, null, events.Add);
        Assert.Equal("not_posted", (await context.ReportScopeChangeAsync(Problem)).Status);
        Assert.Equal(0, handler.Calls);
        Assert.Equal("jira_scope_change", Assert.Single(events).Type);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeveloperJiraAccessRequiresEnabledJiraAgent(bool jiraEnabled, bool streaming)
    {
        RecordingHandler handler = new();
        List<RunEvent> events = [];
        WorkflowJiraContext context = Create(handler, "WEPOD-42", events.Add);
        WorkflowSettings settings = new("https://example.test", "model", "key");
        Dictionary<string, IChatClient> clients = AgentNames.Required.ToDictionary(
            name => name, _ => (IChatClient)new ToolCheckingClient(jiraEnabled));
        Dictionary<string, WorkflowSettings> settingsByAgent = AgentNames.Required.ToDictionary(name => name, _ => settings);
        if (jiraEnabled)
        {
            clients[AgentNames.Jira] = new ToolCheckingClient(jiraEnabled);
            settingsByAgent[AgentNames.Jira] = settings;
        }
        AgentTeam team = AgentTeamFactory.Create(clients, settingsByAgent, jira: context);
        Assert.Equal(jiraEnabled, team.Jira is not null);
        if (streaming)
        {
            await foreach (var update in team.Developer.RunStreamingAsync("Inspect scope comment availability.")) { }
        }
        else
            await team.Developer.RunAsync("Inspect scope comment availability.");
        Assert.Equal(jiraEnabled ? 1 : 0, handler.Calls);
        Assert.Equal(jiraEnabled ? 1 : 0, events.Count);
        if (jiraEnabled) Assert.Equal("jira_scope_change", events[0].Type);
    }

    private sealed class ToolCheckingClient(bool jiraEnabled) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var tools = options?.Tools ?? [];
            Assert.Equal(jiraEnabled, tools.Any(tool => tool.Name == "report_jira_scope_change"));
            Assert.DoesNotContain(tools, tool => tool.Name == "report_jira_problem" || tool.Name == "ask_jira_clarification");
            if (!jiraEnabled) Assert.DoesNotContain(tools, tool => tool.Name.Contains("jira", StringComparison.Ordinal));
            // A model or custom prompt can still emit the marker without a tool. Disabled Jira
            // must also prevent the automatic reporting wrapper from making an HTTP write.
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "<jira_scope_change>" + Problem + "</jira_scope_change>")));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (ChatResponseUpdate update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RecordingHandler(HttpStatusCode status = HttpStatusCode.Created, bool networkFailure = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Url { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Url = request.RequestUri!.AbsoluteUri;
            Method = request.Method;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (networkFailure) throw new HttpRequestException("private remote error");
            return new(status) { Content = new StringContent("{\"id\":\"321\"}") };
        }
    }
}
