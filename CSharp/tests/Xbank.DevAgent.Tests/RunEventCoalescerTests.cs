using Xbank.DevAgent.Core;

namespace Xbank.DevAgent.Tests;

public sealed class RunEventCoalescerTests
{
    [Fact]
    public async Task BatchesBurstTextWithoutLosingTailOrCrossingAgentAndToolBoundaries()
    {
        RunEvent[] input = [
            .. Enumerable.Range(0, 1000).Select(_ => RunEvent.Delta("developer", "x")),
            RunEvent.Tool("developer", "search"),
            RunEvent.Delta("developer", "after tool"),
            RunEvent.Delta("developer", " tail"),
            RunEvent.Delta("reviewer", "review"),
            RunEvent.Delta("reviewer", " tail")];
        List<RunEvent> output = [];
        await foreach (RunEvent item in RunEventCoalescer.CoalesceAsync(Source(input), new ManualClock())) output.Add(item);
        Assert.Equal("x", output[0].Text);
        Assert.Equal(new string('x', 1000), string.Concat(output.TakeWhile(e => e.Type == "message").Select(e => e.Text)));
        Assert.Equal("search", output[2].Text);
        Assert.Equal("after tool tail", string.Concat(output.Skip(3).Where(e => e.Agent == "developer").Select(e => e.Text)));
        Assert.Equal("review tail", string.Concat(output.Where(e => e.Agent == "reviewer").Select(e => e.Text)));
        Assert.True(output.Count < 10);
    }

    [Fact]
    public async Task FlushesAtIntervalAndSizeLimitAndBeforeSourceFailure()
    {
        ManualClock clock = new();
        async IAsyncEnumerable<RunEvent> Stream()
        {
            yield return RunEvent.Delta("developer", "first");
            yield return RunEvent.Delta("developer", "buffered");
            clock.Advance();
            yield return RunEvent.Delta("developer", " interval");
            yield return RunEvent.Delta("developer", new string('x', 4096));
            yield return RunEvent.Delta("developer", "tail");
            await Task.Yield();
            throw new InvalidOperationException("provider failed");
        }
        List<RunEvent> output = [];
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (RunEvent item in RunEventCoalescer.CoalesceAsync(Stream(), clock)) output.Add(item);
        });
        Assert.Equal(new[] { "first", "buffered interval", new string('x', 4096), "tail" }, output.Select(e => e.Text!));
    }

    private static async IAsyncEnumerable<RunEvent> Source(IEnumerable<RunEvent> events)
    {
        await Task.Yield();
        foreach (RunEvent item in events) yield return item;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance() => _ticks += TimeSpan.FromMilliseconds(250).Ticks;
    }
}
