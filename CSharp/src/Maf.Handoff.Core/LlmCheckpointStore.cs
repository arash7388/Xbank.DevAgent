using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

/// <summary>A durable cache of completed model calls. Incomplete and failed calls are retained for diagnosis.</summary>
public interface ILlmCheckpointStore
{
    ValueTask<IAsyncDisposable> AcquireAsync(string cacheKey, CancellationToken cancellationToken = default);

    Task<LlmCheckpoint?> FindCompletedAsync(string cacheKey, CancellationToken cancellationToken = default);

    Task<string> StartAsync(
        string workflowId,
        string cacheKey,
        string agent,
        string model,
        string inputJson,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        string checkpointId,
        string responseJson,
        bool reusable,
        CancellationToken cancellationToken = default);

    Task FailAsync(string checkpointId, Exception exception, CancellationToken cancellationToken = default);
}

public sealed record LlmCheckpoint(string CacheKey, string ResponseJson);

/// <summary>
/// SQLite-backed model-call checkpoints. Each operation uses a short transaction, so a model call never
/// holds a database lock while waiting on the provider.
/// </summary>
public sealed partial class SqliteLlmCheckpointStore : ILlmCheckpointStore, IToolCallStore
{
    private const int BusyTimeoutSeconds = 15;
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, KeyLock> _keyLocks = new(StringComparer.Ordinal);

    public SqliteLlmCheckpointStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = BusyTimeoutSeconds,
            Pooling = false,
        }.ToString();
        Initialize();
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        KeyLock keyLock;
        while (true)
        {
            keyLock = _keyLocks.GetOrAdd(cacheKey, static _ => new());
            lock (keyLock)
            {
                if (keyLock.Removed)
                {
                    continue;
                }

                keyLock.ReferenceCount++;
                break;
            }
        }

        try
        {
            await keyLock.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new KeyLockLease(this, cacheKey, keyLock);
        }
        catch
        {
            ReleaseReference(cacheKey, keyLock, releaseSemaphore: false);
            throw;
        }
    }

    public async Task<LlmCheckpoint?> FindCompletedAsync(
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT response_json
            FROM llm_checkpoints
            WHERE cache_key = $cache_key AND status = 'completed' AND reusable = 1
            ORDER BY completed_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$cache_key", cacheKey);
        object? response = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return response is string responseJson ? new(cacheKey, responseJson) : null;
    }

    public async Task<string> StartAsync(
        string workflowId,
        string cacheKey,
        string agent,
        string model,
        string inputJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        string checkpointId = Guid.NewGuid().ToString("N");
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO llm_checkpoints (
                id, workflow_id, cache_key, agent_name, model, status, input_json, attempt_number, created_at, updated_at)
            SELECT
                $id, $workflow_id, $cache_key, $agent, $model, 'running', $input_json,
                COALESCE(MAX(attempt_number), 0) + 1, $now, $now
            FROM llm_checkpoints
            WHERE cache_key = $cache_key;
            """;
        string now = DateTimeOffset.UtcNow.ToString("O");
        command.Parameters.AddWithValue("$id", checkpointId);
        command.Parameters.AddWithValue("$workflow_id", workflowId);
        command.Parameters.AddWithValue("$cache_key", cacheKey);
        command.Parameters.AddWithValue("$agent", agent);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$input_json", inputJson);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return checkpointId;
    }

    public async Task CompleteAsync(
        string checkpointId,
        string responseJson,
        bool reusable,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE llm_checkpoints
            SET status = 'completed', response_json = $response_json, error_json = NULL,
                reusable = $reusable, updated_at = $now, completed_at = $now
            WHERE id = $id AND status = 'running';
            """;
        command.Parameters.AddWithValue("$id", checkpointId);
        command.Parameters.AddWithValue("$response_json", responseJson);
        command.Parameters.AddWithValue("$reusable", reusable ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FailAsync(
        string checkpointId,
        Exception exception,
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
            UPDATE llm_checkpoints
            SET status = 'failed', error_json = $error_json, reusable = 0, updated_at = $now
            WHERE id = $id AND status = 'running';
            """;
        command.Parameters.AddWithValue("$id", checkpointId);
        command.Parameters.AddWithValue("$error_json", errorJson);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Initialize()
    {
        using SqliteConnection connection = new(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 15000;

            CREATE TABLE IF NOT EXISTS llm_checkpoints (
                id TEXT PRIMARY KEY,
                workflow_id TEXT NOT NULL,
                cache_key TEXT NOT NULL,
                agent_name TEXT NOT NULL,
                model TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('running', 'completed', 'failed')),
                input_json TEXT NOT NULL,
                response_json TEXT,
                error_json TEXT,
                reusable INTEGER NOT NULL DEFAULT 0 CHECK (reusable IN (0, 1)),
                attempt_number INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                completed_at TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_llm_checkpoints_status_updated
            ON llm_checkpoints(status, updated_at);

            """;
        command.ExecuteNonQuery();

        command.CommandText = "PRAGMA table_info(llm_checkpoints);";
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            bool hasWorkflowId = false;
            while (reader.Read())
            {
                hasWorkflowId |= string.Equals(reader.GetString(1), "workflow_id", StringComparison.Ordinal);
            }

            if (!hasWorkflowId)
            {
                reader.Close();
                command.CommandText = "ALTER TABLE llm_checkpoints ADD COLUMN workflow_id TEXT;";
                command.ExecuteNonQuery();
                command.CommandText = "UPDATE llm_checkpoints SET workflow_id = id WHERE workflow_id IS NULL;";
                command.ExecuteNonQuery();
            }
        }

        command.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_llm_checkpoints_cache_lookup
            ON llm_checkpoints(cache_key, status, reusable, completed_at DESC);

            CREATE INDEX IF NOT EXISTS ix_llm_checkpoints_workflow
            ON llm_checkpoints(workflow_id, created_at);
            """;
        command.ExecuteNonQuery();

        InitializeToolCallSchema(connection);
        InitializeWorkflowSchema(connection);
    }

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 15000; PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 15000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private void ReleaseReference(string cacheKey, KeyLock keyLock, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            keyLock.Semaphore.Release();
        }

        lock (keyLock)
        {
            keyLock.ReferenceCount--;
            if (keyLock.ReferenceCount == 0)
            {
                keyLock.Removed = true;
                _keyLocks.TryRemove(new KeyValuePair<string, KeyLock>(cacheKey, keyLock));
                keyLock.Semaphore.Dispose();
            }
        }
    }

    private sealed class KeyLock
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);

        internal int ReferenceCount { get; set; }

        internal bool Removed { get; set; }
    }

    private sealed class KeyLockLease(
        SqliteLlmCheckpointStore owner,
        string cacheKey,
        KeyLock keyLock) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.ReleaseReference(cacheKey, keyLock, releaseSemaphore: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class LlmCheckpointMetrics
{
    private long _hits;
    private long _providerCalls;

    internal long Hits => Interlocked.Read(ref _hits);

    internal long ProviderCalls => Interlocked.Read(ref _providerCalls);

    internal void RecordHit() => Interlocked.Increment(ref _hits);

    internal void RecordProviderCall() => Interlocked.Increment(ref _providerCalls);
}

internal sealed class CheckpointingChatClient(
    IChatClient innerClient,
    ILlmCheckpointStore store,
    string agent,
    WorkflowSettings settings,
    LlmCheckpointMetrics metrics,
    string workflowId) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatMessage[] requestMessages = [.. messages];
        CheckpointRequest request = CheckpointSerialization.CreateRequest(agent, settings, requestMessages, options);
        await using IAsyncDisposable lease = await store.AcquireAsync(request.CacheKey, cancellationToken)
            .ConfigureAwait(false);
        LlmCheckpoint? checkpoint = await store.FindCompletedAsync(request.CacheKey, cancellationToken)
            .ConfigureAwait(false);
        if (checkpoint is not null)
        {
            metrics.RecordHit();
            return CheckpointSerialization.DeserializeResponse(checkpoint.ResponseJson);
        }

        string checkpointId = await store.StartAsync(
            workflowId,
            request.CacheKey,
            agent,
            settings.Model,
            request.InputJson,
            cancellationToken)
            .ConfigureAwait(false);
        bool providerCallRecorded = false;
        bool requestLimitResponse = false;
        try
        {
            ChatResponse response = await innerClient.GetResponseAsync(requestMessages, options, cancellationToken)
                .ConfigureAwait(false);
            requestLimitResponse = HandoffTopology.IsRequestLimitMessage(response.Text);
            if (!requestLimitResponse)
            {
                metrics.RecordProviderCall();
                providerCallRecorded = true;
            }

            CheckpointResponse serialized = CheckpointSerialization.SerializeResponse(response);
            await store.CompleteAsync(checkpointId, serialized.Json, serialized.Reusable, cancellationToken)
                .ConfigureAwait(false);
            return response;
        }
        catch (Exception exception)
        {
            if (!providerCallRecorded && !requestLimitResponse)
            {
                metrics.RecordProviderCall();
            }

            await RecordFailureAsync(checkpointId, exception).ConfigureAwait(false);
            throw;
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatMessage[] requestMessages = [.. messages];
        CheckpointRequest request = CheckpointSerialization.CreateRequest(agent, settings, requestMessages, options);
        await using IAsyncDisposable lease = await store.AcquireAsync(request.CacheKey, cancellationToken)
            .ConfigureAwait(false);
        LlmCheckpoint? checkpoint = await store.FindCompletedAsync(request.CacheKey, cancellationToken)
            .ConfigureAwait(false);
        if (checkpoint is not null)
        {
            metrics.RecordHit();
            foreach (ChatResponseUpdate update in CheckpointSerialization
                .DeserializeResponse(checkpoint.ResponseJson)
                .ToChatResponseUpdates())
            {
                yield return update;
            }

            yield break;
        }

        string checkpointId = await store.StartAsync(
            workflowId,
            request.CacheKey,
            agent,
            settings.Model,
            request.InputJson,
            cancellationToken)
            .ConfigureAwait(false);
        List<ChatResponseUpdate> updates = [];
        bool completed = false;
        bool failureRecorded = false;
        try
        {
            await using IAsyncEnumerator<ChatResponseUpdate> enumerator = innerClient
                .GetStreamingResponseAsync(requestMessages, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    update = enumerator.Current;
                }
                catch (Exception exception)
                {
                    metrics.RecordProviderCall();
                    await RecordFailureAsync(checkpointId, exception).ConfigureAwait(false);
                    failureRecorded = true;
                    throw;
                }

                updates.Add(update);
                yield return update;
            }

            ChatResponse response = updates.ToChatResponse();
            if (!HandoffTopology.IsRequestLimitMessage(response.Text))
            {
                metrics.RecordProviderCall();
            }

            CheckpointResponse serialized = CheckpointSerialization.SerializeResponse(response);
            await store.CompleteAsync(checkpointId, serialized.Json, serialized.Reusable, cancellationToken)
                .ConfigureAwait(false);
            completed = true;
        }
        finally
        {
            if (!completed && !failureRecorded)
            {
                await RecordFailureAsync(
                    checkpointId,
                    new InvalidOperationException("The model response stream ended before a complete checkpoint was saved."))
                    .ConfigureAwait(false);
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : innerClient.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // This decorator does not own the provider client.
    }

    private async Task RecordFailureAsync(string checkpointId, Exception exception)
    {
        try
        {
            await store.FailAsync(checkpointId, exception, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the provider failure. A later attempt will overwrite the stale running checkpoint.
        }
    }
}

internal sealed record CheckpointRequest(string CacheKey, string InputJson);

internal sealed record CheckpointResponse(string Json, bool Reusable);

internal static class CheckpointSerialization
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static CheckpointRequest CreateRequest(
        string agent,
        WorkflowSettings settings,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options)
    {
        object payload = new
        {
            formatVersion = FormatVersion,
            agent,
            model = settings.Model,
            endpoint = settings.ModelEndpoint,
            provider = settings.Provider is null ? null : new
            {
                settings.Provider.Id,
                settings.Provider.Url,
                settings.Provider.Thinking,
                settings.Provider.ReasoningEffort,
            },
            messages = messages.Select(ProjectMessage),
            options = options is null ? null : new
            {
                options.ConversationId,
                options.Instructions,
                options.Temperature,
                options.MaxOutputTokens,
                options.TopP,
                options.TopK,
                options.FrequencyPenalty,
                options.PresencePenalty,
                options.Seed,
                reasoning = SafeValue(options.Reasoning),
                responseFormat = SafeValue(options.ResponseFormat),
                options.ModelId,
                options.StopSequences,
                options.AllowMultipleToolCalls,
                options.AllowBackgroundResponses,
                continuationToken = SafeValue(options.ContinuationToken),
                toolMode = SafeValue(options.ToolMode),
                tools = options.Tools?.Select(ProjectTool),
                additionalProperties = SafeValue(options.AdditionalProperties),
            },
        };
        JsonElement element = JsonSerializer.SerializeToElement(payload, JsonOptions);
        string inputJson = Canonicalize(element);
        string cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputJson))).ToLowerInvariant();
        return new(cacheKey, inputJson);
    }

    internal static CheckpointResponse SerializeResponse(ChatResponse response)
    {
        bool reusable = !HandoffTopology.IsRequestLimitMessage(response.Text) && response.Messages
            .SelectMany(message => message.Contents)
            .All(IsReusableContent);
        ResponseDto dto = new(
            FormatVersion,
            response.FinishReason?.Value,
            response.ModelId,
            response.Messages.Select(message => new MessageDto(
                message.Role.Value,
                message.AuthorName,
                message.MessageId,
                message.Contents
                    .Where(content => content is not UsageContent)
                    .Select(ProjectResponseContent)
                    .ToArray())).ToArray());
        return new(JsonSerializer.Serialize(dto, JsonOptions), reusable);
    }

    internal static ChatResponse DeserializeResponse(string json)
    {
        ResponseDto dto = JsonSerializer.Deserialize<ResponseDto>(json, JsonOptions)
            ?? throw new InvalidDataException("The stored LLM checkpoint response is empty.");
        if (dto.FormatVersion != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported LLM checkpoint format version {dto.FormatVersion}.");
        }

        List<ChatMessage> messages = dto.Messages.Select(message =>
        {
            ChatMessage restored = new(
                new ChatRole(message.Role),
                message.Contents.Select(RestoreContent).ToList())
            {
                AuthorName = message.AuthorName,
                MessageId = message.MessageId,
            };
            return restored;
        }).ToList();
        return new ChatResponse(messages)
        {
            FinishReason = string.IsNullOrWhiteSpace(dto.FinishReason)
                ? null
                : new ChatFinishReason(dto.FinishReason),
            ModelId = dto.ModelId,
        };
    }

    private static object ProjectMessage(ChatMessage message) => new
    {
        role = message.Role.Value,
        message.AuthorName,
        message.MessageId,
        contents = message.Contents.Select(ProjectInputContent),
    };

    private static object ProjectInputContent(AIContent content) => content switch
    {
        TextContent text => new { kind = "text", value = (object?)text.Text },
        TextReasoningContent reasoning => new { kind = "reasoning", value = (object?)reasoning.Text },
        FunctionCallContent call => new
        {
            kind = "function_call",
            callId = call.CallId,
            call.Name,
            value = SafeValue(call.Arguments),
        },
        FunctionResultContent result => new
        {
            kind = "function_result",
            callId = result.CallId,
            value = SafeValue(result.Result),
        },
        DataContent data => new
        {
            kind = "data",
            data.MediaType,
            data.Name,
            value = (object?)data.Uri,
        },
        _ => new
        {
            kind = content.GetType().FullName,
            value = SafeValue(content),
        },
    };

    private static object ProjectTool(AITool tool) => tool is AIFunctionDeclaration function
        ? new
        {
            kind = tool.GetType().FullName,
            function.Name,
            function.Description,
            schema = function.JsonSchema.ValueKind == JsonValueKind.Undefined
                ? null
                : function.JsonSchema.GetRawText(),
        }
        : new
        {
            kind = tool.GetType().FullName,
            tool.Name,
            tool.Description,
            schema = (string?)null,
        };

    private static ContentDto ProjectResponseContent(AIContent content) => content switch
    {
        TextContent text => new("text", Text: text.Text),
        TextReasoningContent reasoning => new("reasoning", Text: reasoning.Text),
        FunctionCallContent call => new(
            "function_call",
            CallId: call.CallId,
            Name: call.Name,
            Value: ToElement(call.Arguments),
            InformationalOnly: call.InformationalOnly),
        UsageContent usage => new("usage", Value: ToElement(usage.Details)),
        _ => new(content.GetType().FullName ?? "unknown", Value: ToElement(content)),
    };

    private static AIContent RestoreContent(ContentDto content) => content.Kind switch
    {
        "text" => new TextContent(content.Text ?? string.Empty),
        "reasoning" => new TextReasoningContent(content.Text ?? string.Empty),
        "function_call" => new FunctionCallContent(
            content.CallId ?? throw new InvalidDataException("Cached function call has no call ID."),
            content.Name ?? throw new InvalidDataException("Cached function call has no name."),
            content.Value?.Deserialize<Dictionary<string, object?>>(JsonOptions)
                ?? new Dictionary<string, object?>())
        {
            InformationalOnly = content.InformationalOnly ?? false,
        },
        _ => throw new InvalidDataException($"Cached content kind '{content.Kind}' cannot be restored."),
    };

    private static bool IsReusableContent(AIContent content) =>
        content is TextContent or TextReasoningContent or FunctionCallContent or UsageContent;

    private static object? SafeValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.SerializeToElement(value, value.GetType(), JsonOptions);
        }
        catch (Exception exception) when (exception is NotSupportedException or JsonException)
        {
            return value.ToString();
        }
    }

    private static JsonElement? ToElement(object? value)
    {
        object? safe = SafeValue(value);
        return safe is null ? null : JsonSerializer.SerializeToElement(safe, safe.GetType(), JsonOptions);
    }

    private static string Canonicalize(JsonElement element)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            WriteCanonical(writer, element);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private sealed record ResponseDto(
        int FormatVersion,
        string? FinishReason,
        string? ModelId,
        IReadOnlyList<MessageDto> Messages);

    private sealed record MessageDto(
        string Role,
        string? AuthorName,
        string? MessageId,
        IReadOnlyList<ContentDto> Contents);

    private sealed record ContentDto(
        string Kind,
        string? Text = null,
        string? CallId = null,
        string? Name = null,
        JsonElement? Value = null,
        bool? InformationalOnly = null);
}
