using System.Text.Json;
using Maf.Handoff.Core;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class ToolResultCacheTests
{
    [Fact]
    public async Task CanonicalArgumentsShareResultsButVersionsAndRepresentationsDoNot()
    {
        ToolResultCache cache = new();
        int calls = 0;
        ValueTask<object?> Read(CancellationToken _) => ValueTask.FromResult<object?>("value " + ++calls);
        AIFunctionArguments first = new() { ["query"] = "hello", ["options"] = new { a = 1, b = 2 } };
        AIFunctionArguments second = new() { ["options"] = JsonSerializer.SerializeToElement(new { b = 2, a = 1 }), ["query"] = "hello" };
        string Key(string scope, string version, AIFunctionArguments args) => ToolResultCache.CreateKey(scope, version, args);
        var a = await cache.InvokeAsync(Key("full", "v1", first), TimeSpan.FromMinutes(1), Read, default);
        var b = await cache.InvokeAsync(Key("full", "v1", second), TimeSpan.FromMinutes(1), Read, default);
        Assert.False(a.Hit);
        Assert.True(b.Hit);
        Assert.Equal(a.Result, b.Result);
        Assert.False((await cache.InvokeAsync(Key("compact", "v1", first), TimeSpan.FromMinutes(1), Read, default)).Hit);
        Assert.False((await cache.InvokeAsync(Key("full", "v2", first), TimeSpan.FromMinutes(1), Read, default)).Hit);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ConcurrentReadsShareOneExecutionAndCancellingAWaiterDoesNotCancelIt()
    {
        ToolResultCache cache = new();
        TaskCompletionSource<object?> finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        async ValueTask<object?> Read(CancellationToken token) { Interlocked.Increment(ref calls); return await finish.Task.WaitAsync(token); }
        Task<(object? Result, bool Hit)> owner = cache.InvokeAsync("key", TimeSpan.FromMinutes(1), Read, default).AsTask();
        using CancellationTokenSource waiterCancellation = new();
        Task<(object? Result, bool Hit)> cancelled = cache.InvokeAsync("key", TimeSpan.FromMinutes(1), Read, waiterCancellation.Token).AsTask();
        Task<(object? Result, bool Hit)> follower = cache.InvokeAsync("key", TimeSpan.FromMinutes(1), Read, default).AsTask();
        waiterCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        finish.SetResult("shared");
        Assert.False((await owner).Hit);
        Assert.True((await follower).Hit);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExpiryAndCapacityEvictionExecuteFreshReads()
    {
        ManualClock clock = new();
        ToolResultCache cache = new(clock, maxEntries: 1);
        int calls = 0;
        ValueTask<object?> Read(CancellationToken _) => ValueTask.FromResult<object?>("result " + ++calls);
        await cache.InvokeAsync("one", TimeSpan.FromSeconds(30), Read, default);
        Assert.True((await cache.InvokeAsync("one", TimeSpan.FromSeconds(30), Read, default)).Hit);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False((await cache.InvokeAsync("one", TimeSpan.FromSeconds(30), Read, default)).Hit);
        await cache.InvokeAsync("two", TimeSpan.FromSeconds(30), Read, default);
        Assert.False((await cache.InvokeAsync("one", TimeSpan.FromSeconds(30), Read, default)).Hit);
        Assert.Equal(4, calls);
    }

    [Theory]
    [InlineData("{\"error\":\"unavailable\"}")]
    [InlineData("Source file does not exist: Missing.cs")]
    [InlineData("{\"status\":\"failed\"}")]
    public async Task ErrorResultsAreNotRetained(string error)
    {
        ToolResultCache cache = new();
        int calls = 0;
        ValueTask<object?> Read(CancellationToken _) { calls++; return ValueTask.FromResult<object?>(error); }
        await cache.InvokeAsync("key", TimeSpan.FromMinutes(1), Read, default);
        Assert.False((await cache.InvokeAsync("key", TimeSpan.FromMinutes(1), Read, default)).Hit);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ExceptionsAndCancelledExecutionsDoNotPoisonTheCache()
    {
        ToolResultCache cache = new();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.InvokeAsync("key", TimeSpan.FromMinutes(1),
            _ => throw new InvalidOperationException("failed"), default).AsTask());
        using CancellationTokenSource cancellation = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.InvokeAsync("key", TimeSpan.FromMinutes(1),
            token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return ValueTask.FromResult<object?>("unused"); }, cancellation.Token).AsTask());
        Assert.False((await cache.InvokeAsync("key", TimeSpan.FromMinutes(1), _ => ValueTask.FromResult<object?>("recovered"), default)).Hit);
    }

    [Fact]
    public async Task OversizedResultsAreNotRetainedAndMutableResultsAreSnapshotted()
    {
        ToolResultCache small = new(maxBytes: 10);
        ValueTask<object?> Large(CancellationToken _) => ValueTask.FromResult<object?>(new string('x', 100));
        await small.InvokeAsync("large", TimeSpan.FromMinutes(1), Large, default);
        Assert.False((await small.InvokeAsync("large", TimeSpan.FromMinutes(1), Large, default)).Hit);
        ToolResultCache cache = new();
        List<string> items = ["original"];
        ValueTask<object?> Mutable(CancellationToken _) => ValueTask.FromResult<object?>(items);
        await cache.InvokeAsync("mutable", TimeSpan.FromMinutes(1), Mutable, default);
        items.Add("changed");
        var cached = await cache.InvokeAsync("mutable", TimeSpan.FromMinutes(1), Mutable, default);
        Assert.True(cached.Hit);
        Assert.Equal("[\"original\"]", ((JsonElement)cached.Result!).GetRawText());
    }

    [Fact]
    public async Task JiraReadsExpireAndWritesAndUserDecisionsAlwaysInvalidateThem()
    {
        ManualClock clock = new();
        ToolResultCache cache = new(clock);
        int reads = 0, writes = 0;
        AIFunction read = new CachedReadAIFunction(
            AIFunctionFactory.Create(() => "read " + ++reads, name: "get_jira_issue_comments"),
            cache, _ => cache.JiraVersion, "jira", TimeSpan.FromSeconds(30));
        AIFunction write = new JiraCacheInvalidatingAIFunction(
            AIFunctionFactory.Create(() => "write " + ++writes, name: "report_jira_problem"), cache);
        AIFunctionArguments args = new();
        await read.InvokeAsync(args);
        await read.InvokeAsync(args);
        Assert.Equal(1, reads);
        await write.InvokeAsync(args);
        await write.InvokeAsync(args);
        Assert.Equal(2, writes);
        await read.InvokeAsync(args);
        Assert.Equal(2, reads);
        clock.Advance(TimeSpan.FromSeconds(30));
        await read.InvokeAsync(args);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task UnknownGraphVersionDisablesCachingAndIndexChangesRefreshIt()
    {
        ToolResultCache cache = new();
        int calls = 0, hits = 0;
        string? version = null;
        AIFunction tool = new CachedReadAIFunction(
            AIFunctionFactory.Create(() => "graph " + ++calls, name: "get_context"),
            cache, _ => version, "graph", TimeSpan.FromHours(1),
            (_, _, _, _) => { hits++; return Task.CompletedTask; });
        AIFunctionArguments args = new();
        await tool.InvokeAsync(args);
        await tool.InvokeAsync(args);
        Assert.Equal(2, calls);
        version = "index-1";
        await tool.InvokeAsync(args);
        await tool.InvokeAsync(args);
        Assert.Equal(3, calls);
        Assert.Equal(1, hits);
        version = "index-2";
        await tool.InvokeAsync(args);
        Assert.Equal(4, calls);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
