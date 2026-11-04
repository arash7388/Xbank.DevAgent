using Xbank.DevAgent.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class ToolCallStoreTests
{
    [Fact]
    public async Task AuditingFunctionStoresCompletedInvocation()
    {
        string directory = CreateTemporaryDirectory();
        string databasePath = Path.Combine(directory, "checkpoints.db");
        try
        {
            SqliteLlmCheckpointStore store = new(databasePath);
            AIFunction function = AIFunctionFactory.Create(
                (Func<string, string>)(path => $"content:{path}"),
                name: "read_repository_file");
            ToolCallAuditingAIFunction audited = new(function, store, "workflow-35", "planner");

            object? result = await audited.InvokeAsync(new AIFunctionArguments
            {
                ["path"] = "src/example.cs",
            });

            ToolCallRow row = ReadToolCall(databasePath);
            Assert.Equal("workflow-35", row.WorkflowId);
            Assert.Equal("planner", row.AgentName);
            Assert.Equal("read_repository_file", row.ToolName);
            Assert.Contains("src/example.cs", row.InputJson, StringComparison.Ordinal);
            Assert.Contains("content:src/example.cs", row.ResponseJson, StringComparison.Ordinal);
            Assert.Equal("completed", row.Status);
            Assert.Equal(string.Empty, row.ErrorJson);
            Assert.True(row.DurationMilliseconds >= 0);
            Assert.NotNull(result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AuditingFunctionStoresFailedInvocationAndPreservesFailure()
    {
        string directory = CreateTemporaryDirectory();
        string databasePath = Path.Combine(directory, "checkpoints.db");
        try
        {
            SqliteLlmCheckpointStore store = new(databasePath);
            AIFunction function = AIFunctionFactory.Create(
                (Func<string>)(() => throw new InvalidOperationException("Tool exploded.")),
                name: "failing_tool");
            ToolCallAuditingAIFunction audited = new(function, store, "workflow-failed", "developer");

            Exception exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
                await audited.InvokeAsync(new AIFunctionArguments()));

            ToolCallRow row = ReadToolCall(databasePath);
            Assert.Equal("failed", row.Status);
            Assert.Equal(string.Empty, row.ResponseJson);
            Assert.Contains("Tool exploded.", row.ErrorJson, StringComparison.Ordinal);
            Assert.Contains("Tool exploded.", exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ToolCallRow ReadToolCall(string databasePath)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT workflow_id, agent_name, tool_name, input_json,
                   COALESCE(response_json, ''), status, COALESCE(error_json, ''), duration_ms
            FROM tool_calls
            LIMIT 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7));
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "maf-tool-call-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed record ToolCallRow(
        string WorkflowId,
        string AgentName,
        string ToolName,
        string InputJson,
        string ResponseJson,
        string Status,
        string ErrorJson,
        long DurationMilliseconds);
}
