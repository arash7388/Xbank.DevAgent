using Microsoft.Data.Sqlite;

namespace Maf.Handoff.Core;

/// <summary>Persists immutable workflow inputs and the lifecycle of each execution.</summary>
public interface IWorkflowRunStore
{
    void Create(Guid runId, string workflowType, string configJson, DateTimeOffset createdAt);

    void UpdateStatus(Guid runId, string status, string? error = null);

    WorkflowRunLog GetLog(Guid runId);

    void SaveSnapshot(Guid runId, string snapshotJson);
    string? GetSnapshot(Guid runId);
    void AppendEvent(Guid runId, RunEvent runEvent);
    IReadOnlyList<WorkflowRunLogEntry> ListRuns(int limit = 100);
}

public sealed record WorkflowRunLog(
    WorkflowLogEntry Workflow,
    WorkflowRunLogEntry Run,
    IReadOnlyList<AgentRunLogEntry> AgentRuns,
    IReadOnlyList<ToolCallLogEntry> ToolCalls,
    IReadOnlyList<AiCallLogEntry>? AiCalls = null,
    IReadOnlyList<CheckpointHitLogEntry>? CheckpointHits = null,
    RunUsageMetrics? Usage = null,
    IReadOnlyList<RunEvent>? Events = null,
    IReadOnlyList<RunStatusLogEntry>? StatusHistory = null);

public sealed record RunStatusLogEntry(string Status, string? Error, DateTimeOffset OccurredAt);

public sealed record WorkflowLogEntry(
    string Id,
    string Type,
    int ConfigVersion,
    string ConfigJson,
    DateTimeOffset CreatedAt);

public sealed record WorkflowRunLogEntry(
    string Id,
    string WorkflowId,
    string Status,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record AgentRunLogEntry(
    string Id,
    string Agent,
    string Model,
    string Status,
    int Attempt,
    string InputJson,
    string? ResponseJson,
    string? ErrorJson,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record ToolCallLogEntry(
    string Id,
    string Agent,
    string Tool,
    string Status,
    string InputJson,
    string? ResponseJson,
    string? ErrorJson,
    long? DurationMilliseconds,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed partial class SqliteLlmCheckpointStore : IWorkflowRunStore
{
    public void Create(Guid runId, string workflowType, string configJson, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowType);
        ArgumentException.ThrowIfNullOrWhiteSpace(configJson);

        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        string workflowId = Guid.NewGuid().ToString("N");
        string now = createdAt.ToString("O");

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO workflows (id, workflow_type, config_version, config_json, created_at)
                VALUES ($id, $workflow_type, 1, $config_json, $created_at);

                INSERT INTO workflow_runs (id, workflow_id, status, created_at, updated_at)
                VALUES ($run_id, $id, 'created', $created_at, $created_at);
                """;
            command.Parameters.AddWithValue("$id", workflowId);
            command.Parameters.AddWithValue("$run_id", runId.ToString("D"));
            command.Parameters.AddWithValue("$workflow_type", workflowType);
            command.Parameters.AddWithValue("$config_json", configJson);
            command.Parameters.AddWithValue("$created_at", now);
            command.ExecuteNonQuery();
        }

        SaveRunUsage(connection, transaction, runId.ToString("D"));
        using (SqliteCommand history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText = "INSERT INTO run_status_history(run_id,status,occurred_at) VALUES($run,'created',$now)";
            history.Parameters.AddWithValue("$run", runId.ToString("D"));
            history.Parameters.AddWithValue("$now", now);
            history.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void UpdateStatus(Guid runId, string status, string? error = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO run_status_history(run_id,status,error,occurred_at)
            SELECT id,$status,$error,$updated_at FROM workflow_runs
            WHERE id=$id AND (status != $status OR error IS NOT $error);

            UPDATE workflow_runs
            SET status = $status, error = $error, updated_at = $updated_at,
                started_at = CASE WHEN $status = 'running' AND started_at IS NULL THEN $updated_at ELSE started_at END,
                finished_at = CASE WHEN $status IN (
                    'awaiting_approval', 'no_changes', 'failed', 'cancelled', 'rejected',
                    'approval_failed', 'committed', 'pushed', 'pull_request_created')
                    THEN $updated_at ELSE finished_at END
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public WorkflowRunLog GetLog(Guid runId)
    {
        string workflowId = runId.ToString("D");
        WorkflowLogEntry? workflow = null;
        WorkflowRunLogEntry? run = null;
        List<AgentRunLogEntry> agentRuns = [];
        List<ToolCallLogEntry> toolCalls = [];
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT w.id, w.workflow_type, w.config_version, w.config_json, w.created_at,
                       r.id, r.status, r.error, r.created_at, r.updated_at, r.started_at, r.finished_at
                FROM workflow_runs r
                JOIN workflows w ON w.id = r.workflow_id
                WHERE r.id = $run_id;
                """;
            command.Parameters.AddWithValue("$run_id", workflowId);
            using SqliteDataReader reader = command.ExecuteReader();
            if (reader.Read())
            {
                workflow = new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                    reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4)));
                run = new(reader.GetString(5), reader.GetString(0), reader.GetString(6),
                    ReadNullableString(reader, 7), DateTimeOffset.Parse(reader.GetString(8)),
                    DateTimeOffset.Parse(reader.GetString(9)), ReadNullableDateTimeOffset(reader, 10),
                    ReadNullableDateTimeOffset(reader, 11));
            }
        }

        if (workflow is null || run is null)
        {
            throw new KeyNotFoundException($"Workflow run '{runId:D}' was not found.");
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, agent_name, model, status, attempt_number, input_json,
                       response_json, error_json, created_at, completed_at
                FROM llm_checkpoints
                WHERE workflow_id = $workflow_id
                ORDER BY created_at, attempt_number;
                """;
            command.Parameters.AddWithValue("$workflow_id", workflowId);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                agentRuns.Add(new(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt32(4), reader.GetString(5), ReadNullableString(reader, 6),
                    ReadNullableString(reader, 7), DateTimeOffset.Parse(reader.GetString(8)),
                    ReadNullableDateTimeOffset(reader, 9)));
            }
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, agent_name, tool_name, status, input_json, response_json,
                       error_json, duration_ms, started_at, completed_at
                FROM tool_calls
                WHERE workflow_id = $workflow_id
                ORDER BY started_at;
                """;
            command.Parameters.AddWithValue("$workflow_id", workflowId);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                toolCalls.Add(new(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), ReadNullableString(reader, 5), ReadNullableString(reader, 6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7), DateTimeOffset.Parse(reader.GetString(8)),
                    ReadNullableDateTimeOffset(reader, 9)));
            }
        }

        List<AiCallLogEntry> calls = ReadAuditRows<AiCallLogEntry>(connection, transaction, "ai_calls", workflowId);
        List<CheckpointHitLogEntry> hits = ReadAuditRows<CheckpointHitLogEntry>(connection, transaction, "checkpoint_hits", workflowId);
        RunUsageMetrics? usage = null;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT summary_json FROM run_usage WHERE run_id=$run";
            command.Parameters.AddWithValue("$run", workflowId);
            if (command.ExecuteScalar() is string json)
                usage = System.Text.Json.JsonSerializer.Deserialize<RunUsageMetrics>(json, AuditJson);
        }
        List<RunStatusLogEntry> history = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT status,error,occurred_at FROM run_status_history WHERE run_id=$run ORDER BY sequence";
            command.Parameters.AddWithValue("$run", workflowId);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) history.Add(new(reader.GetString(0), ReadNullableString(reader, 1), DateTimeOffset.Parse(reader.GetString(2))));
        }
        return new(workflow, run, agentRuns, toolCalls, calls, hits, usage, ReadEvents(connection, workflowId, transaction), history);
    }

    public void SaveSnapshot(Guid runId, string snapshotJson)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO run_snapshots VALUES($run,$json) ON CONFLICT(run_id) DO UPDATE SET snapshot_json=$json";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        command.Parameters.AddWithValue("$json", snapshotJson);
        command.ExecuteNonQuery();
    }

    public string? GetSnapshot(Guid runId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM run_snapshots WHERE run_id=$run";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        return command.ExecuteScalar() as string;
    }

    public void AppendEvent(Guid runId, RunEvent runEvent)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO run_events(run_id,event_json) VALUES($run,$json)";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        command.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(runEvent, AuditJson));
        command.ExecuteNonQuery();
    }

    private static List<RunEvent> ReadEvents(SqliteConnection connection, string runId, SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT event_json FROM run_events WHERE run_id=$run ORDER BY sequence";
        command.Parameters.AddWithValue("$run", runId);
        using SqliteDataReader reader = command.ExecuteReader();
        List<RunEvent> events = [];
        while (reader.Read()) events.Add(System.Text.Json.JsonSerializer.Deserialize<RunEvent>(reader.GetString(0), AuditJson)!);
        return events;
    }

    public IReadOnlyList<WorkflowRunLogEntry> ListRuns(int limit = 100)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id,workflow_id,status,error,created_at,updated_at,started_at,finished_at FROM workflow_runs ORDER BY created_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using SqliteDataReader reader = command.ExecuteReader();
        List<WorkflowRunLogEntry> runs = [];
        while (reader.Read()) runs.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), ReadNullableString(reader, 3),
            DateTimeOffset.Parse(reader.GetString(4)), DateTimeOffset.Parse(reader.GetString(5)),
            ReadNullableDateTimeOffset(reader, 6), ReadNullableDateTimeOffset(reader, 7)));
        return runs;
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));

    private static void InitializeWorkflowSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS workflows (
                id TEXT PRIMARY KEY,
                workflow_type TEXT NOT NULL,
                config_version INTEGER NOT NULL,
                config_json TEXT NOT NULL CHECK (json_valid(config_json)),
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS workflow_runs (
                id TEXT PRIMARY KEY,
                workflow_id TEXT NOT NULL REFERENCES workflows(id),
                status TEXT NOT NULL,
                error TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                started_at TEXT,
                finished_at TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_workflows_type_created
            ON workflows(workflow_type, created_at DESC);

            CREATE INDEX IF NOT EXISTS ix_workflow_runs_workflow_created
            ON workflow_runs(workflow_id, created_at DESC);

            CREATE INDEX IF NOT EXISTS ix_workflow_runs_status_updated
            ON workflow_runs(status, updated_at DESC);
            """;
        command.ExecuteNonQuery();
    }
}
