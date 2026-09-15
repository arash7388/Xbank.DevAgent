using Microsoft.Data.Sqlite;

namespace Maf.Handoff.Core.Tests;

public sealed class WorkflowRunStoreTests
{
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
