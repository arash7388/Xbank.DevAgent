using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

public interface IAiCallStore
{
    string StartAiCall(string runId, string agent, WorkflowSettings settings, string inputJson, bool streaming);
    void FinishAiCall(string id, ChatResponse? response, UsageDetails? usage, Exception? error);
    void RecordCheckpointHit(string runId, string agent, string model, string cacheKey);
}

public sealed record AiCallLogEntry(
    string Id, string RunId, string Agent, string Model, string Endpoint,
    string Status, bool Streaming, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    long? DurationMilliseconds, string InputJson, string? ResponseJson,
    UsageDetails? Usage, ModelProviderDefinition? Pricing,
    decimal? EstimatedCostUsd, string? ResponseId, string? FinishReason, string? ErrorJson);

public sealed record RunUsageMetrics(
    long AiCalls, long CompletedCalls, long FailedCalls, long CancelledCalls, long PendingCalls,
    long CallsWithUsage, long CallsWithoutUsage, long CallsWithCacheHits, long CallsWithCacheMisses,
    long CallsWithUnknownCacheUsage, long CheckpointHits, long CheckpointMisses,
    long InputTokens, long CachedInputTokens, long UncachedInputTokens, long OutputTokens,
    long ReasoningTokens, long TotalTokens, decimal? EstimatedCostUsd,
    TokenUsageSummary TokenUsage, long InputTokensWithUnknownCacheUsage);

public sealed record CheckpointHitLogEntry(string Agent, string Model, string CacheKey, DateTimeOffset OccurredAt);

public sealed partial class SqliteLlmCheckpointStore : IAiCallStore
{
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    private static void InitializeAiCallSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ai_calls (
                id TEXT PRIMARY KEY, run_id TEXT NOT NULL, agent TEXT NOT NULL, model TEXT NOT NULL,
                status TEXT NOT NULL, started_at TEXT NOT NULL, finished_at TEXT,
                input_tokens INTEGER, cached_input_tokens INTEGER, uncached_input_tokens INTEGER,
                output_tokens INTEGER, reasoning_tokens INTEGER, total_tokens INTEGER,
                estimated_cost_usd TEXT, record_json TEXT NOT NULL CHECK(json_valid(record_json))
            );
            CREATE INDEX IF NOT EXISTS ix_ai_calls_run ON ai_calls(run_id, started_at);
            CREATE TABLE IF NOT EXISTS checkpoint_hits (
                id TEXT PRIMARY KEY, run_id TEXT NOT NULL, record_json TEXT NOT NULL CHECK(json_valid(record_json))
            );
            CREATE INDEX IF NOT EXISTS ix_checkpoint_hits_run ON checkpoint_hits(run_id);
            CREATE TABLE IF NOT EXISTS run_usage (
                run_id TEXT PRIMARY KEY, updated_at TEXT NOT NULL,
                summary_json TEXT NOT NULL CHECK(json_valid(summary_json))
            );
            CREATE TABLE IF NOT EXISTS run_snapshots (
                run_id TEXT PRIMARY KEY, snapshot_json TEXT NOT NULL CHECK(json_valid(snapshot_json))
            );
            CREATE TABLE IF NOT EXISTS run_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL,
                event_json TEXT NOT NULL CHECK(json_valid(event_json))
            );
            CREATE INDEX IF NOT EXISTS ix_run_events_run ON run_events(run_id, sequence);
            CREATE TABLE IF NOT EXISTS run_status_history (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL,
                status TEXT NOT NULL, error TEXT, occurred_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_run_status_history_run ON run_status_history(run_id,sequence);
            """;
        command.ExecuteNonQuery();
    }

    public string StartAiCall(string runId, string agent, WorkflowSettings settings, string inputJson, bool streaming)
    {
        AiCallLogEntry call = new(Guid.NewGuid().ToString("N"), runId, agent, settings.Model,
            settings.ModelEndpoint, "running", streaming, DateTimeOffset.UtcNow, null, null,
            inputJson, null, null, settings.Provider,
            AifaRequestContext.IsAifa(new Uri(settings.ModelEndpoint)) ? 0m : null, null, null, null);
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        SaveAiCall(connection, transaction, call);
        SaveRunUsage(connection, transaction, runId);
        transaction.Commit();
        return call.Id;
    }

    public void FinishAiCall(string id, ChatResponse? response, UsageDetails? usage, Exception? error)
    {
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT record_json FROM ai_calls WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        AiCallLogEntry call = JsonSerializer.Deserialize<AiCallLogEntry>((string)command.ExecuteScalar()!, AuditJson)!;
        if (call.Status != "running") return;
        DateTimeOffset finished = DateTimeOffset.UtcNow;
        call = call with
        {
            Status = error is OperationCanceledException ? "cancelled" : error is null ? "completed" : "failed",
            FinishedAt = finished,
            DurationMilliseconds = (long)(finished - call.StartedAt).TotalMilliseconds,
            ResponseJson = response is null ? null : CheckpointSerialization.SerializeResponse(response).Json,
            Usage = usage,
            EstimatedCostUsd = AifaRequestContext.IsAifa(new Uri(call.Endpoint)) ? 0m : EstimateCost(usage, call.Pricing),
            ResponseId = response?.ResponseId,
            FinishReason = response?.FinishReason?.Value,
            ErrorJson = error is null ? null : JsonSerializer.Serialize(new { type = error.GetType().FullName, error.Message, detail = error.ToString() }, AuditJson),
        };
        SaveAiCall(connection, transaction, call);
        SaveRunUsage(connection, transaction, call.RunId);
        transaction.Commit();
    }

    public void RecordCheckpointHit(string runId, string agent, string model, string cacheKey)
    {
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO checkpoint_hits VALUES ($id, $run, $json)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(new CheckpointHitLogEntry(agent, model, cacheKey, DateTimeOffset.UtcNow), AuditJson));
        command.ExecuteNonQuery();
        SaveRunUsage(connection, transaction, runId);
        transaction.Commit();
    }

    private static decimal? EstimateCost(UsageDetails? usage, ModelProviderDefinition? price)
    {
        if (usage?.InputTokenCount is not long input || usage.OutputTokenCount is not long output ||
            price?.InputCostPerMillionTokens is not decimal inputRate || price.OutputCostPerMillionTokens is not decimal outputRate)
            return null;
        long cached = Math.Clamp(usage.CachedInputTokenCount ?? 0, 0, input);
        return ((input - cached) * inputRate + cached * (price.CachedInputCostPerMillionTokens ?? inputRate) + output * outputRate) / 1_000_000m;
    }

    private static void SaveAiCall(SqliteConnection connection, SqliteTransaction transaction, AiCallLogEntry call)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ai_calls VALUES ($id,$run,$agent,$model,$status,$start,$end,$input,$cached,$miss,$output,$reasoning,$total,$cost,$json)
            ON CONFLICT(id) DO UPDATE SET status=$status,finished_at=$end,input_tokens=$input,cached_input_tokens=$cached,
                uncached_input_tokens=$miss,output_tokens=$output,reasoning_tokens=$reasoning,total_tokens=$total,
                estimated_cost_usd=$cost,record_json=$json;
            """;
        void Add(string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        Add("$id", call.Id); Add("$run", call.RunId); Add("$agent", call.Agent); Add("$model", call.Model);
        Add("$status", call.Status); Add("$start", call.StartedAt.ToString("O")); Add("$end", call.FinishedAt?.ToString("O"));
        Add("$input", call.Usage?.InputTokenCount); Add("$cached", call.Usage?.CachedInputTokenCount);
        Add("$miss", call.Usage?.CachedInputTokenCount is long hit && call.Usage.InputTokenCount is long input ? Math.Max(0, input - hit) : null);
        Add("$output", call.Usage?.OutputTokenCount); Add("$reasoning", call.Usage?.ReasoningTokenCount); Add("$total", call.Usage?.TotalTokenCount);
        Add("$cost", call.EstimatedCostUsd?.ToString(CultureInfo.InvariantCulture)); Add("$json", JsonSerializer.Serialize(call, AuditJson));
        command.ExecuteNonQuery();
    }

    private static List<T> ReadAuditRows<T>(SqliteConnection connection, SqliteTransaction? transaction, string table, string runId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT record_json FROM {table} WHERE run_id=$run ORDER BY rowid";
        command.Parameters.AddWithValue("$run", runId);
        using SqliteDataReader reader = command.ExecuteReader();
        List<T> rows = [];
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), AuditJson)!);
        return rows;
    }

    private static void SaveRunUsage(SqliteConnection connection, SqliteTransaction transaction, string runId)
    {
        List<AiCallLogEntry> calls = ReadAuditRows<AiCallLogEntry>(connection, transaction, "ai_calls", runId);
        List<CheckpointHitLogEntry> hits = ReadAuditRows<CheckpointHitLogEntry>(connection, transaction, "checkpoint_hits", runId);
        RunUsageMetrics summary = SummarizeCalls(calls, hits.Count);
        summary = summary with { TokenUsage = summary.TokenUsage.WithCheckpoints(hits) };
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO run_usage VALUES($run,$now,$json) ON CONFLICT(run_id) DO UPDATE SET updated_at=$now,summary_json=$json";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(summary, AuditJson));
        command.ExecuteNonQuery();
    }

    private static RunUsageMetrics SummarizeCalls(IReadOnlyList<AiCallLogEntry> calls, long checkpointHits)
    {
        AgentTokenUsage[] agents = calls.GroupBy(call => (call.Agent, call.Model)).Select(group =>
        {
            AiCallLogEntry[] items = group.ToArray();
            long input = items.Sum(call => call.Usage?.InputTokenCount ?? 0);
            long cached = items.Sum(call => Math.Clamp(call.Usage?.CachedInputTokenCount ?? 0, 0, call.Usage?.InputTokenCount ?? 0));
            long output = items.Sum(call => call.Usage?.OutputTokenCount ?? 0);
            long reasoning = items.Sum(call => Math.Clamp(call.Usage?.ReasoningTokenCount ?? 0, 0, call.Usage?.OutputTokenCount ?? 0));
            long total = items.Sum(call => call.Usage?.TotalTokenCount ?? (call.Usage?.InputTokenCount ?? 0) + (call.Usage?.OutputTokenCount ?? 0));
            decimal? cost = items.All(call => call.EstimatedCostUsd.HasValue) ? items.Sum(call => call.EstimatedCostUsd!.Value) : null;
            return new AgentTokenUsage(group.Key.Agent, group.Key.Model, items.Length, input, cached, output, reasoning, total, cost);
        }).OrderBy(item => item.Agent, StringComparer.Ordinal).ToArray();
        TokenUsageSummary tokens = new(agents, calls.Count, agents.Sum(a => a.InputTokens), agents.Sum(a => a.CachedInputTokens),
            agents.Sum(a => a.OutputTokens), agents.Sum(a => a.ReasoningTokens), agents.Sum(a => a.TotalTokens),
            agents.All(a => a.EstimatedCostUsd.HasValue) ? agents.Sum(a => a.EstimatedCostUsd ?? 0m) : null);
        return new(calls.Count, calls.Count(c => c.Status == "completed"), calls.Count(c => c.Status == "failed"),
            calls.Count(c => c.Status == "cancelled"), calls.Count(c => c.Status == "running"),
            calls.Count(c => c.Usage is not null), calls.Count(c => c.Usage is null),
            calls.Count(c => c.Usage?.CachedInputTokenCount > 0),
            calls.Count(c => c.Usage?.CachedInputTokenCount is long hit && c.Usage.InputTokenCount is long input && input > hit),
            calls.Count(c => c.Usage?.CachedInputTokenCount is null), checkpointHits, calls.Count,
            tokens.InputTokens, tokens.CachedInputTokens,
            calls.Sum(c => c.Usage?.CachedInputTokenCount is long hit && c.Usage.InputTokenCount is long input ? Math.Max(0, input - hit) : 0),
            tokens.OutputTokens, tokens.ReasoningTokens, tokens.TotalTokens, tokens.EstimatedCostUsd, tokens,
            calls.Where(c => c.Usage?.CachedInputTokenCount is null).Sum(c => c.Usage?.InputTokenCount ?? 0));
    }
}
