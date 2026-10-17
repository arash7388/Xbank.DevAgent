using System.Runtime.CompilerServices;
using System.Text.Json;
using Maf.Handoff.Core;
using Maf.Handoff.Api;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Maf.Handoff.Tests;

public sealed class AiCallStoreTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PersistsZeroCostForAifaEvenWithoutPricingOrCompleteUsage(int usageKind)
    {
        using DatabaseFixture db = new();
        Guid run = Guid.NewGuid();
        db.Store.Create(run, "test", "{}", DateTimeOffset.UtcNow);
        WorkflowSettings settings = new(AifaRequestContext.Endpoint, AifaRequestContext.Model, "key",
            Provider: usageKind == 0 ? null : Price with { Url = AifaRequestContext.Endpoint });
        string id = db.Store.StartAiCall(run.ToString("D"), AgentNames.Developer, settings, "{}", false);
        Assert.Equal(0m, db.Store.GetLog(run).Usage!.EstimatedCostUsd);
        UsageDetails? usage = usageKind switch
        {
            1 => new() { TotalTokenCount = 100 },
            2 => new() { InputTokenCount = 80, OutputTokenCount = 20, TotalTokenCount = 100 },
            _ => null,
        };
        db.Store.FinishAiCall(id, new(new ChatMessage(ChatRole.Assistant, "Done")), usage, null);
        WorkflowRunLog log = new SqliteLlmCheckpointStore(db.Path).GetLog(run);
        Assert.Equal(0m, Assert.Single(log.AiCalls!).EstimatedCostUsd);
        Assert.Equal(0m, log.Usage!.EstimatedCostUsd);
        Assert.Equal(0m, Assert.Single(log.Usage.TokenUsage.ByAgent).EstimatedCostUsd);
        Assert.Equal(usageKind == 0 ? 0 : 100, log.Usage.TotalTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayOnlyAgentAppearsInLiveAndArchivedSummaryWithoutProviderCost(bool streaming)
    {
        using DatabaseFixture db = new();
        Guid run = Guid.NewGuid();
        db.Store.Create(run, "test", "{}", DateTimeOffset.UtcNow);
        StubClient provider = new();
        var warmup = new CheckpointingChatClient(provider, db.Store, AgentNames.Jira, Settings, new(), "warmup");
        ChatMessage[] input = [new(ChatRole.User, "Investigate the Jira issue")];
        await warmup.GetResponseAsync(input);
        TokenUsageCollector collector = new(new Dictionary<string, WorkflowSettings> { [AgentNames.Jira] = Settings });
        var replay = new CheckpointingChatClient(provider, db.Store, AgentNames.Jira, Settings, new(), run.ToString("D"), collector);
        if (streaming) await foreach (var update in replay.GetStreamingResponseAsync(input)) { }
        else await replay.GetResponseAsync(input);

        Assert.Equal(1, provider.Calls);
        var live = collector.Snapshot();
        // Older stored summaries omitted replay-only agents. Rebuild their breakdown from audit hits.
        var saved = db.Store.GetLog(run).Usage!;
        using (var connection = new SqliteConnection($"Data Source={db.Path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE run_usage SET summary_json=$json WHERE run_id=$run";
            command.Parameters.AddWithValue("$run", run.ToString("D"));
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(
                saved with { TokenUsage = TokenUsageSummary.Empty with { EstimatedCostUsd = 0m } },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            command.ExecuteNonQuery();
        }
        var archived = new SqliteLlmCheckpointStore(db.Path).GetLog(run).Usage!.TokenUsage;
        foreach (var summary in new[] { live, archived })
        {
            Assert.Equal(0, summary.LlmRequestCount);
            Assert.Equal(0, summary.TotalTokens);
            Assert.Equal(0m, summary.EstimatedCostUsd);
            Assert.Equal(1, summary.CheckpointHits);
            var jira = Assert.Single(summary.ByAgent);
            Assert.Equal(AgentNames.Jira, jira.Agent);
            Assert.Equal(1, jira.CheckpointHits);
            Assert.Equal(0m, jira.EstimatedCostUsd);
        }
    }

    private static readonly ModelProviderDefinition Price = new("model", "Test", "https://example.test",
        true, false, 1000, 1000, false, null, [], 2m, 8m, 0.5m);
    private static readonly WorkflowSettings Settings = new("https://example.test", "model", "secret-not-persisted", Provider: Price);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistsUsageAndCheckpointHitsAcrossRestartWithoutDoubleBilling(bool streaming)
    {
        using DatabaseFixture db = new();
        Guid run = Guid.NewGuid();
        db.Store.Create(run, "test", "{}", DateTimeOffset.UtcNow);
        StubClient provider = new();
        TokenUsageCollector collector = new(new Dictionary<string, WorkflowSettings> { [AgentNames.Developer] = Settings });
        IChatClient durable = new DurableUsageChatClient(provider, db.Store, run.ToString("D"), AgentNames.Developer, Settings);
        IChatClient tracking = new UsageTrackingChatClient(durable, AgentNames.Developer, collector);
        IChatClient client = new CheckpointingChatClient(tracking, db.Store, AgentNames.Developer, Settings, new(), run.ToString("D"));
        async Task Request()
        {
            if (streaming) await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "Hello")])) { }
            else await client.GetResponseAsync([new(ChatRole.User, "Hello")]);
        }
        await Request();
        await Request();

        db.Store.AppendEvent(run, RunEvent.Trace("developer", "done"));
        db.Store.SaveSnapshot(run, "{\"diff\":\"persisted patch\"}");
        SqliteLlmCheckpointStore reopened = new(db.Path);
        WorkflowRunLog log = reopened.GetLog(run);
        AiCallLogEntry call = Assert.Single(log.AiCalls!);
        Assert.Equal("completed", call.Status);
        Assert.Equal(120, call.Usage!.InputTokenCount);
        Assert.Equal(20, call.Usage.CachedInputTokenCount);
        Assert.Equal(Price, call.Pricing! with { SupportsReasoningEffort = Price.SupportsReasoningEffort });
        Assert.Equal(0.00045m, call.EstimatedCostUsd);
        Assert.NotNull(call.DurationMilliseconds);
        Assert.DoesNotContain(Settings.ApiKey, call.InputJson);
        using JsonDocument response = JsonDocument.Parse(call.ResponseJson!);
        Assert.Equal(150, response.RootElement.GetProperty("usage").GetProperty("totalTokenCount").GetInt64());
        RunUsageMetrics summary = Assert.IsType<RunUsageMetrics>(log.Usage);
        Assert.Equal(1, summary.AiCalls);
        Assert.Equal(1, summary.CheckpointHits);
        Assert.Equal(1, summary.TokenUsage.CheckpointHits);
        Assert.Equal(1, Assert.Single(summary.TokenUsage.ByAgent).CheckpointHits);
        Assert.Equal(1, summary.CheckpointMisses);
        Assert.Equal(1, summary.CallsWithCacheHits);
        Assert.Equal(1, summary.CallsWithCacheMisses);
        Assert.Equal(100, summary.UncachedInputTokens);
        Assert.Equal(150, summary.TotalTokens);
        Assert.Equal(0.00045m, summary.EstimatedCostUsd);
        Assert.Equal(collector.Snapshot().TotalTokens, summary.TotalTokens);
        Assert.Single(log.Events!);
        Assert.Contains("persisted patch", reopened.GetSnapshot(run));
        Assert.Single(reopened.ListRuns());
        Assert.Equal(1, provider.Calls);
        using SqliteConnection connection = new($"Data Source={db.Path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT total_tokens,cached_input_tokens,uncached_input_tokens FROM ai_calls";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(150, reader.GetInt64(0));
        Assert.Equal(20, reader.GetInt64(1));
        Assert.Equal(100, reader.GetInt64(2));
    }

    [Fact]
    public async Task SavesFailuresAndMissingUsageWithoutInventingTokensOrCost()
    {
        using DatabaseFixture db = new();
        Guid run = Guid.NewGuid();
        db.Store.Create(run, "test", "{}", DateTimeOffset.UtcNow);
        IChatClient client = new DurableUsageChatClient(new StubClient(fail: true), db.Store, run.ToString("D"), "developer", Settings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new(ChatRole.User, "Fail")]));
        string pending = db.Store.StartAiCall(run.ToString("D"), "developer", Settings, "{}", false);
        db.Store.FinishAiCall(pending, new(new ChatMessage(ChatRole.Assistant, "No usage")), null, null);
        WorkflowRunLog log = new SqliteLlmCheckpointStore(db.Path).GetLog(run);
        Assert.Equal(2, log.Usage!.AiCalls);
        Assert.Equal(1, log.Usage.FailedCalls);
        Assert.Equal(2, log.Usage.CallsWithoutUsage);
        Assert.Equal(2, log.Usage.CallsWithUnknownCacheUsage);
        Assert.Null(log.Usage.EstimatedCostUsd);
        Assert.Null(log.AiCalls![0].Usage);
        Assert.Contains("provider failed", log.AiCalls[0].ErrorJson);
        Assert.NotNull(log.AiCalls[0].FinishedAt);
    }

    [Fact]
    public async Task DisposingStreamRetainsPartialUsageAndCancelledStatus()
    {
        using DatabaseFixture db = new();
        Guid run = Guid.NewGuid();
        db.Store.Create(run, "test", "{}", DateTimeOffset.UtcNow);
        IChatClient client = new DurableUsageChatClient(new StubClient(), db.Store, run.ToString("D"), "developer", Settings);
        await using (var stream = client.GetStreamingResponseAsync([new(ChatRole.User, "Stream")]).GetAsyncEnumerator())
            Assert.True(await stream.MoveNextAsync());
        WorkflowRunLog log = new SqliteLlmCheckpointStore(db.Path).GetLog(run);
        Assert.Equal(1, log.Usage!.CancelledCalls);
        Assert.NotNull(Assert.Single(log.AiCalls!).FinishedAt);
    }

    [Fact]
    public void ReadsArchivedSessionAndAllEventsAfterRestart()
    {
        using DatabaseFixture db = new();
        Guid run = Guid.NewGuid();
        db.Store.Create(run, "test", "{}", DateTimeOffset.UtcNow);
        db.Store.UpdateStatus(run, "awaiting_approval");
        string call = db.Store.StartAiCall(run.ToString("D"), "developer", Settings, "{}", false);
        db.Store.FinishAiCall(call, null, new() { InputTokenCount = 100, CachedInputTokenCount = 40, OutputTokenCount = 20, TotalTokenCount = 120 }, null);
        for (int i = 0; i < 510; i++) db.Store.AppendEvent(run, RunEvent.Trace("developer", $"event {i}"));
        SessionSnapshot saved = new(run, "https://example.test/repo", "gitlab", "main", "task", DateTimeOffset.UtcNow,
            "awaiting_approval", [], "saved diff", null, null, null, null);
        db.Store.SaveSnapshot(run, JsonSerializer.Serialize(saved));
        SqliteLlmCheckpointStore reopened = new(db.Path);
        // Archive reads only need the durable run store; there is no live SessionState.
        RepositorySessionService sessions = new(null!, null!, null!, null!, null!, null!, reopened, reopened, new ConfigurationBuilder().Build());
        SessionSnapshot restored = sessions.Get(run);
        Assert.Equal("saved diff", restored.Diff);
        Assert.Equal("awaiting_approval", restored.Status);
        Assert.Equal(510, restored.Events.Count);
        Assert.Equal("event 509", restored.Events[^1].Text);
        Assert.Equal(120, restored.Usage!.TotalTokens);
        Assert.Equal(1, restored.Usage.LlmRequestCount);
    }

    [Fact]
    public void PersistsZeroCallsForRunsThatFailBeforeContactingProvider()
    {
        using DatabaseFixture db = new();
        Guid run = Guid.NewGuid();
        db.Store.Create(run, "test", "{}", DateTimeOffset.UtcNow);
        db.Store.UpdateStatus(run, "failed", "clone failed");
        RunUsageMetrics summary = db.Store.GetLog(run).Usage!;
        Assert.Equal(0, summary.AiCalls);
        Assert.Equal(0, summary.TotalTokens);
        Assert.Equal(0m, summary.EstimatedCostUsd);
    }

    [Fact]
    public async Task UpgradePreservesOlderRecordsAndDoesNotInventHistoricalUsage()
    {
        using DatabaseFixture db = new();
        Guid run = Guid.NewGuid();
        db.Store.Create(run, "legacy", "{}", DateTimeOffset.UtcNow);
        string id = await db.Store.StartAsync(run.ToString("D"), "key", "developer", "model", "{}");
        await db.Store.CompleteAsync(id, "{\"formatVersion\":1,\"messages\":[]}", true);
        using (SqliteConnection connection = new($"Data Source={db.Path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE ai_calls; DROP TABLE checkpoint_hits; DROP TABLE run_usage; DROP TABLE run_events; DROP TABLE run_snapshots; DROP TABLE run_status_history;";
            command.ExecuteNonQuery();
        }
        SqliteLlmCheckpointStore reopened = new(db.Path);
        WorkflowRunLog log = reopened.GetLog(run);
        Assert.Single(log.AgentRuns);
        Assert.Null(log.Usage);
        Assert.Empty(log.AiCalls!);
        Assert.Null(reopened.GetSnapshot(run));
        reopened.AppendEvent(run, RunEvent.Trace(null, "upgraded"));
        Assert.Single(reopened.GetLog(run).Events!);
    }

    private sealed class StubClient(bool fail = false) : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (fail) throw new InvalidOperationException("provider failed");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done"))
            {
                ResponseId = "provider-response", ModelId = "model", FinishReason = ChatFinishReason.Stop,
                Usage = new() { InputTokenCount = 120, CachedInputTokenCount = 20, OutputTokenCount = 30, ReasoningTokenCount = 10, TotalTokenCount = 150 },
            });
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
        }
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }

    private sealed class DatabaseFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"maf-ai-audit-{Guid.NewGuid():N}.db");
        public SqliteLlmCheckpointStore Store { get; }
        public DatabaseFixture() => Store = new(Path);
        public void Dispose()
        {
            foreach (string file in new[] { Path, Path + "-wal", Path + "-shm" })
                if (File.Exists(file)) File.Delete(file);
        }
    }
}
