using System.Runtime.CompilerServices;
using System.Text.Json;
using Maf.Handoff.Api;
using Maf.Handoff.Core;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class JiraIntakeCheckpointTests
{
    private static readonly WorkflowSettings Settings = new("https://example.test", "model", "key");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusesCompletedIntakeAcrossRestartWithoutProviderTokens(bool streaming)
    {
        using Fixture fixture = new();
        Jira jira = new();
        Client provider = new();
        TokenUsageCollector usage = Usage();
        LlmCheckpointMetrics metrics = new();
        ChatResponse original = await Respond(fixture.Wrap(provider, jira), Input(), Options(), streaming);
        SqliteLlmCheckpointStore reopened = new(fixture.Path);
        JiraIntakeCheckpointChatClient replay = new(provider, reopened, jira, "TASK-1", "scope", Settings,
            Guid.NewGuid().ToString("D"), metrics, usage);
        ChatResponse cached = await Respond(replay, Input(), Options(), streaming);

        Assert.Equal(1, provider.Calls);
        Assert.Contains("READY: task evidence", cached.Text);
        Assert.NotEqual(Call(original).CallId, Call(cached).CallId);
        Assert.Null(cached.Usage);
        Assert.Equal(1, usage.Snapshot().CheckpointHits);
        Assert.Equal(0, usage.Snapshot().LlmRequestCount);
        Assert.Equal(0, usage.Snapshot().TotalTokens);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("comment_body")]
    [InlineData("comment_author")]
    [InlineData("attachment_bytes")]
    [InlineData("attachment_name")]
    [InlineData("status")]
    [InlineData("scope")]
    [InlineData("task")]
    [InlineData("instructions")]
    [InlineData("model")]
    public async Task ChangedEvidenceOrConfigurationPreventsReuse(string change)
    {
        using Fixture fixture = new();
        Jira jira = new();
        Client provider = new();
        await fixture.Wrap(provider, jira).GetResponseAsync(Input(), Options());
        switch (change)
        {
            case "title": jira.Title += " changed"; break;
            case "description": jira.Description += " changed"; break;
            case "comment_body": jira.Comment += " changed"; break;
            case "comment_author": jira.Author = "another author"; break;
            case "attachment_bytes": jira.AttachmentHash = "different source bytes, same extracted text and size"; break;
            case "attachment_name": jira.FileName = "renamed.txt"; break;
            case "status": jira.Status = "Done"; break;
        }
        ChatOptions options = Options();
        if (change == "instructions") options.Instructions += " changed";
        await fixture.Wrap(provider, jira, change == "scope" ? "other-repository" : "scope",
            change == "model" ? Settings with { Model = "other-model" } : Settings)
            .GetResponseAsync(Input(change == "task" ? "Additional task requirement" : null), options);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task CommentsBeyondFirstPageArePartOfFingerprint()
    {
        Jira jira = new() { CommentCount = 101 };
        JiraIntakeFingerprint original = (await JiraIntakeFingerprint.ReadAsync(jira, "TASK-1", default))!;
        Assert.Equal(new[] { 0, 100 }, jira.Offsets);
        jira.LastComment = "changed last page";
        JiraIntakeFingerprint changed = (await JiraIntakeFingerprint.ReadAsync(jira, "TASK-1", default))!;
        Assert.NotEqual(original.CommentsHash, changed.CommentsHash);
    }

    [Fact]
    public async Task SelectionExcludesUnselectedCommentsAndAttachmentsFromFingerprint()
    {
        Jira jira = new();
        IJiraIssueSearch selected = new SelectedJiraComments(new SelectedJiraAttachments(jira, "TASK-1", []), false);
        JiraIntakeFingerprint original = (await JiraIntakeFingerprint.ReadAsync(selected, "TASK-1", default))!;
        jira.Comment = "excluded comment changed";
        jira.AttachmentHash = "excluded bytes changed";
        JiraIntakeFingerprint changed = (await JiraIntakeFingerprint.ReadAsync(selected, "TASK-1", default))!;
        Assert.Equal(original, changed);
        Assert.Empty(jira.Offsets);
        Assert.Equal(0, jira.AttachmentReads);
    }

    [Theory]
    [InlineData("pagination")]
    [InlineData("attachment")]
    [InlineData("read_error")]
    public async Task UnverifiableSnapshotFallsBackToNormalIntake(string failure)
    {
        using Fixture fixture = new();
        Jira jira = new();
        Client provider = new();
        await fixture.Wrap(provider, jira).GetResponseAsync(Input(), Options());
        jira.BrokenPagination = failure == "pagination";
        jira.IncompleteAttachment = failure == "attachment";
        jira.FailReads = failure == "read_error";
        await fixture.Wrap(provider, jira).GetResponseAsync(Input(), Options());
        Assert.Equal(2, provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevalidatesRepositoryEvidenceAndPreservesItForPlanner(bool changed)
    {
        using Fixture fixture = new();
        Jira jira = new();
        Client provider = new() { ReadFirst = true };
        string source = "original repository contents";
        ChatOptions options = Options();
        AIFunction read = AIFunctionFactory.Create(() => source, name: "read_repository_file");
        options.Tools!.Add(read);
        var cold = fixture.Wrap(provider, jira);
        ChatMessage[] history = Input();
        ChatResponse readCall = await cold.GetResponseAsync(history, options);
        FunctionCallContent call = Call(readCall);
        history = [.. history, .. readCall.Messages,
            new(ChatRole.Tool, [new FunctionResultContent(call.CallId, await read.InvokeAsync(new()))])];
        await cold.GetResponseAsync(history, options);
        if (changed) source = "changed repository contents";
        ChatResponse result = await fixture.Wrap(provider, jira).GetResponseAsync(Input(), options);
        Assert.Equal(changed ? 3 : 2, provider.Calls);
        if (!changed) Assert.Contains("original repository contents", result.Text);
    }

    [Theory]
    [InlineData("ask_jira_clarification")]
    [InlineData("report_jira_problem")]
    [InlineData("unknown_tool")]
    public async Task HumanDecisionsAndSideEffectsAreNeverReused(string name)
    {
        using Fixture fixture = new();
        Jira jira = new();
        Client provider = new();
        var cold = fixture.Wrap(provider, jira);
        await cold.GetResponseAsync(Input(), Options());
        // A different initial task keeps this scenario separate from the already completed entry.
        cold = fixture.Wrap(provider, jira);
        ChatMessage[] input = Input("decision-dependent task");
        provider.ReadFirst = true;
        provider.ReadTool = name;
        provider.ResetRound();
        ChatResponse first = await cold.GetResponseAsync(input, Options());
        FunctionCallContent call = Call(first);
        await cold.GetResponseAsync([.. input, .. first.Messages,
            new(ChatRole.Tool, [new FunctionResultContent(call.CallId, "answer")])], Options());
        int calls = provider.Calls;
        await fixture.Wrap(provider, jira).GetResponseAsync(Input("decision-dependent task"), Options());
        Assert.Equal(calls + 1, provider.Calls);
    }

    [Fact]
    public async Task FailedStreamDoesNotSavePartialHandoff()
    {
        using Fixture fixture = new();
        Jira jira = new();
        Client provider = new() { FailStream = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Respond(fixture.Wrap(provider, jira), Input(), Options(), true));
        provider.FailStream = false;
        await Respond(fixture.Wrap(provider, jira), Input(), Options(), true);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task WorkflowReusesIntakeAndHandsEvidenceToPlanner()
    {
        using Fixture fixture = new();
        Jira jira = new();
        Client intake = new() { ReadFirst = true, ReadTool = "get_jira_issue_details" };
        TerminalClient planner = new();
        string[] names = [AgentNames.Jira, .. AgentNames.Required];
        Dictionary<string, IChatClient> clients = names.ToDictionary(name => name,
            name => name == AgentNames.Jira ? (IChatClient)intake : planner);
        Dictionary<string, WorkflowSettings> settings = names.ToDictionary(name => name, _ => Settings);
        for (int iteration = 0; iteration < 2; iteration++)
        {
            var store = new SqliteLlmCheckpointStore(fixture.Path);
            var context = new JiraIntakeCheckpoint(store, jira, "TASK-1", "scope", Settings,
                Guid.NewGuid().ToString("D"), new(), Usage());
            WorkflowBundle bundle = HandoffWorkflowFactory.Create(clients, settings, jira: jira, jiraIntakeCheckpoint: context);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            await using StreamingRun run = await InProcessExecution.RunStreamingAsync(bundle.Workflow,
                "Selected Jira issue: TASK-1\n\nImplement the task", cancellationToken: timeout.Token);
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            await foreach (WorkflowEvent item in run.WatchStreamAsync(timeout.Token))
            {
                Assert.IsNotType<WorkflowErrorEvent>(item);
                if (item is ExecutorFailedEvent failed) throw failed.Data ?? new InvalidOperationException("Executor failed");
            }
        }
        Assert.Equal(2, intake.Calls);
        Assert.Contains("READY: task evidence", planner.LastInput);
        Assert.Contains("Description", planner.LastInput);
    }

    private static FunctionCallContent Call(ChatResponse response) => Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
    private static ChatMessage[] Input(string? task = null) => [new(ChatRole.User, task ?? "Selected Jira issue: TASK-1\n\nImplement the task") { MessageId = Guid.NewGuid().ToString("N") }];
    private static ChatOptions Options() => new()
    {
        Instructions = "Jira intake instructions",
        Tools = [AIFunctionFactory.Create((string reasonForHandoff) => reasonForHandoff, name: "handoff_to_planner")],
    };
    private static TokenUsageCollector Usage() => new(new Dictionary<string, WorkflowSettings> { [AgentNames.Jira] = Settings });
    private static async Task<ChatResponse> Respond(IChatClient client, ChatMessage[] input, ChatOptions options, bool streaming)
    {
        if (!streaming) return await client.GetResponseAsync(input, options);
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(input, options)) updates.Add(update);
        return updates.ToChatResponse();
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"maf-jira-intake-{Guid.NewGuid():N}.db");
        private readonly SqliteLlmCheckpointStore _store;
        public Fixture() => _store = new(Path);
        public JiraIntakeCheckpointChatClient Wrap(Client provider, IJiraIssueSearch jira, string scope = "scope", WorkflowSettings? settings = null) =>
            new(provider, _store, jira, "TASK-1", scope, settings ?? Settings, Guid.NewGuid().ToString("D"), new(), Usage());
        public void Dispose()
        {
            foreach (string file in new[] { Path, Path + "-wal", Path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        }
    }

    private sealed class Client : IChatClient
    {
        public int Calls { get; private set; }
        public bool ReadFirst { get; set; }
        public string ReadTool { get; set; } = "read_repository_file";
        public bool FailStream { get; set; }
        private int _round;
        public void ResetRound() => _round = 0;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            bool read = ReadFirst && _round++ == 0;
            ChatResponse response = new(new ChatMessage(ChatRole.Assistant,
                [new TextContent(read ? "Inspect repository" : "READY: task evidence"),
                 new FunctionCallContent(Guid.NewGuid().ToString("N"), read ? ReadTool :
                    options!.Tools!.Last(tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal)).Name,
                    read ? (ReadTool == "get_jira_issue_details" ? new Dictionary<string, object?> { ["issueKey"] = "TASK-1" } : new Dictionary<string, object?>())
                        : new Dictionary<string, object?> { ["reasonForHandoff"] = "READY: task evidence" })]))
                { Usage = new() { InputTokenCount = 100, OutputTokenCount = 20, TotalTokenCount = 120 } };
            return Task.FromResult(response);
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
            if (FailStream) throw new InvalidOperationException("Interrupted stream");
        }
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }

    private sealed class TerminalClient : IChatClient
    {
        public string LastInput = "";
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastInput = string.Join("\n", messages.Select(message => message.Text));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Planning stopped for test.")));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (ChatResponseUpdate update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return update;
        }
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }

    private sealed class Jira : IJiraIssueSearch
    {
        public string Title = "Title", Description = "Description", Status = "Open", Comment = "Comment", Author = "Alice";
        public string AttachmentHash = "source-bytes-sha256", FileName = "notes.txt", LastComment = "Last comment";
        public int CommentCount = 1, AttachmentReads;
        public bool BrokenPagination, IncompleteAttachment, FailReads;
        public List<int> Offsets = [];
        public Task<JsonElement> GetIssueDetailsAsync(string key, CancellationToken cancellationToken = default)
        {
            if (FailReads) throw new InvalidOperationException("Jira unavailable");
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                key, fields = new { summary = Title, description = Description, status = Status,
                    comment = new { comments = new[] { new { id = "0", body = Comment, author = Author } } },
                    attachment = new[] { new { id = "1", filename = FileName } } },
            }));
        }
        public Task<JsonElement> GetIssueCommentsAsync(string key, int startAt = 0, int maxResults = 50, CancellationToken cancellationToken = default)
        {
            Offsets.Add(startAt);
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                startAt, total = CommentCount, comments = Enumerable.Range(startAt, BrokenPagination ? 0 : Math.Min(maxResults, CommentCount - startAt))
                    .Select(index => new { id = index.ToString(), body = index == 100 ? LastComment : Comment, author = Author }).ToArray(),
            }));
        }
        public Task<IReadOnlyList<JiraAttachmentInfo>> GetAttachmentsAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JiraAttachmentInfo>>([new("1", FileName, "text/plain", 5)]);
        public Task<JiraAttachmentText> ReadAttachmentAsync(string key, string id, int offset = 0, CancellationToken cancellationToken = default)
        {
            AttachmentReads++;
            return Task.FromResult(new JiraAttachmentText(key, id, FileName, "notes", 0, IncompleteAttachment ? 20 : 5,
                IncompleteAttachment, false, ContentSha256: IncompleteAttachment ? null : AttachmentHash));
        }
        public Task<IReadOnlyList<JiraIssueInfo>> SearchIssuesAsync(string jql, int maxResults = 10, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetIssueTitleByKeyAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(Title);
    }
}
