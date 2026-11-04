using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Core;

/// <summary>Durably records actual local tool executions for workflow diagnostics.</summary>
public interface IToolCallStore
{
    Task RecordToolCacheHitAsync(string workflowId, string agent, string toolName, string cacheKey,
        string inputJson, CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task<string> StartToolCallAsync(
        string workflowId,
        string agent,
        string toolName,
        string inputJson,
        CancellationToken cancellationToken = default);

    Task CompleteToolCallAsync(
        string toolCallId,
        string responseJson,
        long durationMilliseconds,
        CancellationToken cancellationToken = default);

    Task FailToolCallAsync(
        string toolCallId,
        Exception exception,
        long durationMilliseconds,
        CancellationToken cancellationToken = default);
}

public sealed partial class SqliteLlmCheckpointStore
{
    public async Task RecordToolCacheHitAsync(string workflowId, string agent, string toolName, string cacheKey,
        string inputJson, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tool_cache_hits (id, workflow_id, agent_name, tool_name, cache_key, input_json, occurred_at)
            VALUES ($id, $workflow, $agent, $tool, $key, $input, $time);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$workflow", workflowId);
        command.Parameters.AddWithValue("$agent", agent);
        command.Parameters.AddWithValue("$tool", toolName);
        command.Parameters.AddWithValue("$key", cacheKey);
        command.Parameters.AddWithValue("$input", inputJson);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> StartToolCallAsync(
        string workflowId,
        string agent,
        string toolName,
        string inputJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        string id = Guid.NewGuid().ToString("N");
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tool_calls (
                id, workflow_id, agent_name, tool_name, input_json, status, started_at)
            VALUES ($id, $workflow_id, $agent, $tool_name, $input_json, 'running', $started_at);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$workflow_id", workflowId);
        command.Parameters.AddWithValue("$agent", agent);
        command.Parameters.AddWithValue("$tool_name", toolName);
        command.Parameters.AddWithValue("$input_json", inputJson);
        command.Parameters.AddWithValue("$started_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task CompleteToolCallAsync(
        string toolCallId,
        string responseJson,
        long durationMilliseconds,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tool_calls
            SET status = 'completed', response_json = $response_json, error_json = NULL,
                duration_ms = $duration_ms, completed_at = $completed_at
            WHERE id = $id AND status = 'running';
            """;
        command.Parameters.AddWithValue("$id", toolCallId);
        command.Parameters.AddWithValue("$response_json", responseJson);
        command.Parameters.AddWithValue("$duration_ms", durationMilliseconds);
        command.Parameters.AddWithValue("$completed_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FailToolCallAsync(
        string toolCallId,
        Exception exception,
        long durationMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string errorJson = JsonSerializer.Serialize(new
        {
            type = exception.GetType().FullName,
            message = exception.Message,
        });

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tool_calls
            SET status = 'failed', response_json = NULL, error_json = $error_json,
                duration_ms = $duration_ms, completed_at = $completed_at
            WHERE id = $id AND status = 'running';
            """;
        command.Parameters.AddWithValue("$id", toolCallId);
        command.Parameters.AddWithValue("$error_json", errorJson);
        command.Parameters.AddWithValue("$duration_ms", durationMilliseconds);
        command.Parameters.AddWithValue("$completed_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void InitializeToolCallSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tool_calls (
                id TEXT PRIMARY KEY,
                workflow_id TEXT NOT NULL,
                agent_name TEXT NOT NULL,
                tool_name TEXT NOT NULL,
                input_json TEXT NOT NULL,
                response_json TEXT,
                error_json TEXT,
                status TEXT NOT NULL CHECK (status IN ('running', 'completed', 'failed')),
                duration_ms INTEGER,
                started_at TEXT NOT NULL,
                completed_at TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_tool_calls_workflow
            ON tool_calls(workflow_id, started_at);

            CREATE INDEX IF NOT EXISTS ix_tool_calls_agent_tool
            ON tool_calls(agent_name, tool_name, started_at);

            CREATE TABLE IF NOT EXISTS tool_cache_hits (
                id TEXT PRIMARY KEY, workflow_id TEXT NOT NULL, agent_name TEXT NOT NULL,
                tool_name TEXT NOT NULL, cache_key TEXT NOT NULL, input_json TEXT NOT NULL,
                occurred_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tool_cache_hits_workflow ON tool_cache_hits(workflow_id, occurred_at);
            """;
        command.ExecuteNonQuery();
    }
}

internal sealed class ToolCallAuditingAIFunction(
    AIFunction innerFunction,
    IToolCallStore store,
    string workflowId,
    string agent) : DelegatingAIFunction(innerFunction)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        string inputJson = Serialize(arguments);
        string? auditId = await TryStartAsync(inputJson, cancellationToken).ConfigureAwait(false);
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            object? result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            if (auditId is not null)
            {
                await TryCompleteAsync(auditId, Serialize(result), stopwatch.ElapsedMilliseconds).ConfigureAwait(false);
            }

            return result;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            if (auditId is not null)
            {
                await TryFailAsync(auditId, exception, stopwatch.ElapsedMilliseconds).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task<string?> TryStartAsync(string inputJson, CancellationToken cancellationToken)
    {
        try
        {
            return await store.StartToolCallAsync(
                workflowId, agent, Name, inputJson, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task TryCompleteAsync(string id, string responseJson, long durationMilliseconds)
    {
        try
        {
            await store.CompleteToolCallAsync(
                id, responseJson, durationMilliseconds, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Tool auditing must not change a successful tool result.
        }
    }

    private async Task TryFailAsync(string id, Exception exception, long durationMilliseconds)
    {
        try
        {
            await store.FailToolCallAsync(
                id, exception, durationMilliseconds, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original tool failure.
        }
    }

    private static string Serialize(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value, JsonOptions);
        }
        catch (Exception exception)
        {
            return JsonSerializer.Serialize(new
            {
                serializationError = exception.Message,
                value = value?.ToString(),
            }, JsonOptions);
        }
    }
}
