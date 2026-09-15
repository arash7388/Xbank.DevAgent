using Microsoft.Data.Sqlite;

namespace Maf.Handoff.Core;

/// <summary>Persists immutable workflow inputs and the lifecycle of each execution.</summary>
public interface IWorkflowRunStore
{
    void Create(Guid runId, string workflowType, string configJson, DateTimeOffset createdAt);

    void UpdateStatus(Guid runId, string status, string? error = null);
}

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

        transaction.Commit();
    }

    public void UpdateStatus(Guid runId, string status, string? error = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
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
    }

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
