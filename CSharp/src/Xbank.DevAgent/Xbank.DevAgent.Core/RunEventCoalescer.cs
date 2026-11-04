using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Xbank.DevAgent.Core;

// Session consumers persist every event. Bound streaming text writes while retaining
// the first chunk immediately and all text before a tool, handoff, error, or completion.
internal static class RunEventCoalescer
{
    internal static async IAsyncEnumerable<RunEvent> CoalesceAsync(
        IAsyncEnumerable<RunEvent> source,
        TimeProvider? timeProvider = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        RunEvent? pending = null;
        StringBuilder text = new();
        long? lastEmission = null;
        await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool next = false;
            ExceptionDispatchInfo? failure = null;
            try { next = await enumerator.MoveNextAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            RunEvent? current = next ? enumerator.Current : null;
            if (pending is not null && (current?.Type != "message" || current.Agent != pending.Agent))
            {
                yield return pending with { Text = text.ToString() };
                pending = null;
                text.Clear();
                lastEmission = null;
            }
            failure?.Throw();
            if (!next) yield break;
            if (current!.Type != "message")
            {
                lastEmission = null;
                yield return current;
                continue;
            }
            pending ??= current;
            text.Append(current.Text);
            if (lastEmission is null || text.Length >= 4096 ||
                clock.GetElapsedTime(lastEmission.Value) >= TimeSpan.FromMilliseconds(250))
            {
                yield return pending with { Text = text.ToString() };
                pending = null;
                text.Clear();
                lastEmission = clock.GetTimestamp();
            }
        }
    }
}
