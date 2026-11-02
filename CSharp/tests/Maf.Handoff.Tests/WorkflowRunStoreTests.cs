using Microsoft.Data.Sqlite;

namespace Maf.Handoff.Core.Tests;

public sealed class WorkflowRunStoreTests
{
    [Theory]
    [InlineData("repository_change")]
    [InlineData("gitlab_review")]
    public void ReturnsSavedWorkflowTypeInHistoryAndDetails(string workflowType)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"maf-workflow-type-{Guid.NewGuid():N}");
        try
        {
            string databasePath = Path.Combine(directory, "checkpoints.db");
            Guid runId = Guid.NewGuid();
            new SqliteLlmCheckpointStore(databasePath).Create(runId, workflowType, "{}", DateTimeOffset.UtcNow);

            SqliteLlmCheckpointStore reopened = new(databasePath);
            Assert.Equal(workflowType, Assert.Single(reopened.ListRuns()).WorkflowType);
            Assert.Equal(workflowType, reopened.GetLog(runId).Run.WorkflowType);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PagesRunsInStableNewestFirstOrder()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"maf-workflow-pages-{Guid.NewGuid():N}");
        try
        {
            SqliteLlmCheckpointStore store = new(Path.Combine(directory, "checkpoints.db"));
            DateTimeOffset createdAt = DateTimeOffset.UtcNow;
            Guid[] ids = Enumerable.Range(0, 53).Select(_ => Guid.NewGuid()).ToArray();
            foreach (Guid id in ids) store.Create(id, "repository_change", "{}", createdAt);

            string[] expected = ids.Select(id => id.ToString("D")).OrderDescending(StringComparer.Ordinal).ToArray();
            var first = store.ListRuns(50);
            var second = store.ListRuns(50, 50);
            Assert.Equal(50, first.Count);
            Assert.Equal(3, second.Count);
            Assert.Equal(expected, first.Concat(second).Select(run => run.Id));
            Assert.Empty(store.ListRuns(50, 100));
            Assert.Equal(first.Select(run => run.Id), store.ListRuns(50, -1).Select(run => run.Id));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StoresAnImmutableConfigSnapshotAndRunLifecycle()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"maf-workflow-store-{Guid.NewGuid():N}");
        string databasePath = Path.Combine(directory, "checkpoints.db");
        try
        {
            SqliteLlmCheckpointStore store = new(databasePath);
            Guid runId = Guid.NewGuid();
            DateTimeOffset createdAt = DateTimeOffset.UtcNow.AddSeconds(-1);

            store.Create(runId, "repository_change", "{\"task\":\"fix it\"}", createdAt);
            store.UpdateStatus(runId, "running");
            store.UpdateStatus(runId, "failed", "provider unavailable");
            store.UpdateStatus(runId, "failed", "provider unavailable");
            Assert.Equal(new[] { "created", "running", "failed" },
                new SqliteLlmCheckpointStore(databasePath).GetLog(runId).StatusHistory!.Select(item => item.Status));

            using (SqliteConnection connection = OpenReadOnly(databasePath))
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT w.workflow_type, w.config_version, w.config_json,
                           r.status, r.error, r.started_at, r.finished_at
                    FROM workflow_runs r
                    JOIN workflows w ON w.id = r.workflow_id
                    WHERE r.id = $id;
                    """;
                command.Parameters.AddWithValue("$id", runId.ToString("D"));
                using SqliteDataReader reader = command.ExecuteReader();

                Assert.True(reader.Read());
                Assert.Equal("repository_change", reader.GetString(0));
                Assert.Equal(1, reader.GetInt32(1));
                Assert.Equal("{\"task\":\"fix it\"}", reader.GetString(2));
                Assert.Equal("failed", reader.GetString(3));
                Assert.Equal("provider unavailable", reader.GetString(4));
                Assert.False(reader.IsDBNull(5));
                Assert.False(reader.IsDBNull(6));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RejectsInvalidConfigJson()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"maf-workflow-store-{Guid.NewGuid():N}");
        try
        {
            SqliteLlmCheckpointStore store = new(Path.Combine(directory, "checkpoints.db"));
            Assert.Throws<SqliteException>(() =>
                store.Create(Guid.NewGuid(), "repository_change", "not-json", DateTimeOffset.UtcNow));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReadsAgentAndToolExecutionLogsForRun()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"maf-workflow-log-{Guid.NewGuid():N}");
        try
        {
            SqliteLlmCheckpointStore store = new(Path.Combine(directory, "checkpoints.db"));
            Guid runId = Guid.NewGuid();
            string workflowId = runId.ToString("D");
            store.Create(runId, "repository_change", "{}", DateTimeOffset.UtcNow);

            string checkpointId = await store.StartAsync(
                workflowId, "cache-key", "developer", "model-a", "{\"messages\":[]}");
            await store.CompleteAsync(checkpointId, "{\"text\":\"done\"}", reusable: false);
            string toolCallId = await store.StartToolCallAsync(
                workflowId, "developer", "read_repository_file", "{\"path\":\"README.md\"}");
            await store.CompleteToolCallAsync(toolCallId, "{\"content\":\"hello\"}", 12);

            WorkflowRunLog log = store.GetLog(runId);

            Assert.Equal("repository_change", log.Workflow.Type);
            Assert.Equal(runId.ToString("D"), log.Run.Id);
            Assert.Equal("created", log.Run.Status);
            AgentRunLogEntry agentRun = Assert.Single(log.AgentRuns);
            Assert.Equal("developer", agentRun.Agent);
            Assert.Equal("completed", agentRun.Status);
            Assert.Equal("{\"text\":\"done\"}", agentRun.ResponseJson);
            ToolCallLogEntry toolCall = Assert.Single(log.ToolCalls);
            Assert.Equal("read_repository_file", toolCall.Tool);
            Assert.Equal(12, toolCall.DurationMilliseconds);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
