using System.Runtime.CompilerServices;
using Maf.Handoff.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class LlmCheckpointStoreTests
{
    private static readonly WorkflowSettings Settings = new(
        "https://example.test",
        "checkpoint-test-model",
        "not-stored");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionBudgetCountsReplaysWithoutChargingProviderUsage(bool streaming)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            WorkflowSettings settings = Settings with { MaxLlmRequests = 2 };
            SqliteLlmCheckpointStore store = new(Path.Combine(directory, "checkpoints.db"));
            RecordingClient provider = new();
            TokenUsageCollector usage = new(new Dictionary<string, WorkflowSettings> { [AgentNames.Developer] = settings });
            LlmCheckpointMetrics metrics = new();
            CheckpointingChatClient checkpointed = new(new UsageTrackingChatClient(provider, AgentNames.Developer, usage),
                store, AgentNames.Developer, settings, metrics, "budget-test");
            WorkflowExecutionGuardChatClient client = new(checkpointed, AgentNames.Developer, new(settings));
            ChatMessage[] input = [new(ChatRole.User, "Implement the requested change.")];

            async Task<ChatResponse> Request() => streaming
                ? (await CollectAsync(client.GetStreamingResponseAsync(input))).ToChatResponse()
                : await client.GetResponseAsync(input);

            await Request();
            await Request();
            ChatResponse stopped = await Request();

            Assert.Equal(1, provider.CallCount);
            Assert.Equal(1, usage.Snapshot().LlmRequestCount);
            Assert.Equal(1, metrics.Hits);
            Assert.True(HandoffTopology.IsRequestLimitMessage(stopped.Text));
            Assert.Equal(1, CountCheckpoints(Path.Combine(directory, "checkpoints.db")));

            // A new workflow gets a fresh budget and may reuse the same successful checkpoint.
            WorkflowExecutionGuardChatClient restarted = new(checkpointed, AgentNames.Developer, new(settings));
            ChatResponse replay = await restarted.GetResponseAsync(input);
            Assert.False(HandoffTopology.IsRequestLimitMessage(replay.Text));
            Assert.Equal(1, provider.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaysCompletedStreamingResponseWithoutAnotherProviderCall()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            SqliteLlmCheckpointStore store = new(Path.Combine(directory, "checkpoints.db"));
            RecordingClient provider = new();
            CheckpointingChatClient client = new(provider, store, "developer", Settings, new(), "workflow-1");
            ChatMessage[] input = [new(ChatRole.User, "Implement the requested change.")];

            ChatResponse first = (await CollectAsync(client.GetStreamingResponseAsync(input))).ToChatResponse();
            CheckpointRow stored = ReadOnlyCheckpoint(Path.Combine(directory, "checkpoints.db"));
            ChatResponse replay = (await CollectAsync(client.GetStreamingResponseAsync(input))).ToChatResponse();

            Assert.Equal(1, provider.CallCount);
            Assert.Equal("completed", stored.Status);
            Assert.Equal("workflow-1", stored.WorkflowId);
            Assert.True(stored.Reusable);
            Assert.Contains("Implement the requested change.", stored.InputJson, StringComparison.Ordinal);
            Assert.Contains("I will edit the file.", stored.ResponseJson, StringComparison.Ordinal);
            Assert.DoesNotContain(Settings.ApiKey, stored.InputJson, StringComparison.Ordinal);
            Assert.Equal("I will edit the file.", first.Text);
            Assert.Equal(first.Text, replay.Text);
            FunctionCallContent call = Assert.Single(replay.Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>());
            Assert.Equal("write_file", call.Name);
            Assert.Equal("call-1", call.CallId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedInputDoesNotReuseCheckpoint()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            SqliteLlmCheckpointStore store = new(Path.Combine(directory, "checkpoints.db"));
            RecordingClient provider = new();
            CheckpointingChatClient client = new(provider, store, "planner", Settings, new(), "workflow-2");

            await CollectAsync(client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "First task")]));
            await CollectAsync(client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "Second task")]));

            Assert.Equal(2, provider.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentIdenticalRequestsShareOneProviderCall()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            SqliteLlmCheckpointStore store = new(Path.Combine(directory, "checkpoints.db"));
            RecordingClient provider = new();
            CheckpointingChatClient client = new(provider, store, "security", Settings, new(), "workflow-3");
            ChatMessage[] input = [new(ChatRole.User, "Check the same patch.")];

            await Task.WhenAll(
                CollectAsync(client.GetStreamingResponseAsync(input)),
                CollectAsync(client.GetStreamingResponseAsync(input)));

            Assert.Equal(1, provider.CallCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedAttemptIsRetriedAndOnlySuccessfulResponseIsReusable()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            SqliteLlmCheckpointStore store = new(Path.Combine(directory, "checkpoints.db"));
            FailsOnceClient provider = new();
            CheckpointingChatClient client = new(provider, store, "reviewer", Settings, new(), "workflow-4");
            ChatMessage[] input = [new(ChatRole.User, "Review this change.")];

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await CollectAsync(client.GetStreamingResponseAsync(input)));
            CheckpointRow failed = ReadOnlyCheckpoint(Path.Combine(directory, "checkpoints.db"));

            ChatResponse recovered = (await CollectAsync(client.GetStreamingResponseAsync(input))).ToChatResponse();
            ChatResponse replay = (await CollectAsync(client.GetStreamingResponseAsync(input))).ToChatResponse();

            Assert.Equal(2, provider.CallCount);
            Assert.Equal(2, CountCheckpoints(Path.Combine(directory, "checkpoints.db")));
            Assert.Equal("failed", failed.Status);
            Assert.False(failed.Reusable);
            Assert.Contains("Transient provider failure.", failed.ErrorJson, StringComparison.Ordinal);
            Assert.Equal("Recovered response", recovered.Text);
            Assert.Equal(recovered.Text, replay.Text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExistingDatabaseIsUpgradedWithWorkflowId()
    {
        string directory = CreateTemporaryDirectory();
        string databasePath = Path.Combine(directory, "checkpoints.db");
        try
        {
            using (SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
            }.ToString()))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE llm_checkpoints (
                        id TEXT PRIMARY KEY, cache_key TEXT NOT NULL, agent_name TEXT NOT NULL,
                        model TEXT NOT NULL, status TEXT NOT NULL, input_json TEXT NOT NULL,
                        response_json TEXT, error_json TEXT, reusable INTEGER NOT NULL DEFAULT 0,
                        attempt_number INTEGER NOT NULL, created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL, completed_at TEXT);
                    INSERT INTO llm_checkpoints
                        (id, cache_key, agent_name, model, status, input_json, attempt_number, created_at, updated_at)
                    VALUES ('legacy-checkpoint', 'key', 'developer', 'model', 'failed', '{}', 1, 'now', 'now');
                    """;
                command.ExecuteNonQuery();
            }

            _ = new SqliteLlmCheckpointStore(databasePath);

            CheckpointRow checkpoint = ReadOnlyCheckpoint(databasePath);
            Assert.Equal("legacy-checkpoint", checkpoint.WorkflowId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<List<ChatResponseUpdate>> CollectAsync(
        IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        List<ChatResponseUpdate> result = [];
        await foreach (ChatResponseUpdate update in updates)
        {
            result.Add(update);
        }

        return result;
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "maf-llm-checkpoint-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static CheckpointRow ReadOnlyCheckpoint(string databasePath)
    {
        using SqliteConnection connection = OpenReadOnly(databasePath);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, input_json, COALESCE(response_json, ''), COALESCE(error_json, ''), reusable, workflow_id
            FROM llm_checkpoints
            LIMIT 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetBoolean(4),
            reader.GetString(5));
    }

    private static long CountCheckpoints(string databasePath)
    {
        using SqliteConnection connection = OpenReadOnly(databasePath);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM llm_checkpoints;";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static SqliteConnection OpenReadOnly(string databasePath) => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());

    private sealed record CheckpointRow(
        string Status,
        string InputJson,
        string ResponseJson,
        string ErrorJson,
        bool Reusable,
        string WorkflowId);

    private sealed class RecordingClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(CreateResponse());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Yield();
            foreach (ChatResponseUpdate update in CreateResponse().ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }

        private static ChatResponse CreateResponse() => new(new ChatMessage(
            ChatRole.Assistant,
            [
                new TextContent("I will edit the file."),
                new FunctionCallContent(
                    "call-1",
                    "write_file",
                    new Dictionary<string, object?> { ["path"] = "src/example.cs" }),
            ]));
    }

    private sealed class FailsOnceClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Yield();
            if (CallCount == 1)
            {
                throw new InvalidOperationException("Transient provider failure.");
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "Recovered response");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
