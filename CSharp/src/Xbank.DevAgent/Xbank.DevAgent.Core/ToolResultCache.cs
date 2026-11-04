using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Core;

// Owned by one team/run. Only explicitly selected read tools are wrapped by the factory.
internal sealed class ToolResultCache(TimeProvider? timeProvider = null, int maxEntries = 128, int maxBytes = 8 * 1024 * 1024)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private long _jiraRevision;
    private int _bytes;

    internal string JiraVersion => Interlocked.Read(ref _jiraRevision).ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal void InvalidateJira() => Interlocked.Increment(ref _jiraRevision);

    internal static string CreateKey(string scope, string version, AIFunctionArguments arguments)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream)) WriteCanonical(writer, document.RootElement);
        return scope + ":" + version + ":" + Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (JsonElement item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    internal async ValueTask<(object? Result, bool Hit)> InvokeAsync(string key, TimeSpan lifetime,
        Func<CancellationToken, ValueTask<object?>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entry entry;
        bool owner;
        lock (_gate)
        {
            foreach (string expired in _entries.Where(pair => pair.Value.CompletedAt is long completed &&
                _clock.GetElapsedTime(completed) >= pair.Value.Lifetime).Select(pair => pair.Key).ToArray()) Remove(expired);
            owner = !_entries.TryGetValue(key, out Entry? existing);
            entry = existing ?? new Entry(lifetime);
            if (owner) _entries.Add(key, entry);
        }
        if (!owner)
        {
            CachedResult shared = await entry.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            // A normal return can still be an error envelope. Do not replay it to followers.
            return shared.Reusable ? (shared.Result, true)
                : (await operation(cancellationToken).ConfigureAwait(false), false);
        }

        try
        {
            object? result = await operation(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            object? snapshot = result;
            int bytes = 0;
            bool reusable = false;
            try
            {
                JsonElement json = JsonSerializer.SerializeToElement(result);
                reusable = IsSuccessful(json);
                bytes = Encoding.UTF8.GetByteCount(json.GetRawText());
                // Retain immutable results; never share mutable Jira collections between callers.
                snapshot = result is string ? result : json;
            }
            catch (Exception error) when (error is JsonException or NotSupportedException) { }
            lock (_gate)
            {
                bool retain = reusable && bytes <= maxBytes && maxEntries > 0;
                if (retain)
                {
                    while (_entries.Count > maxEntries || _bytes + bytes > maxBytes)
                    {
                        string? oldest = _entries.Where(pair => pair.Value.CompletedAt is not null)
                            .OrderBy(pair => pair.Value.CompletedAt).Select(pair => pair.Key).FirstOrDefault();
                        if (oldest is null) { retain = false; break; }
                        Remove(oldest);
                    }
                }
                if (retain)
                {
                    entry.Bytes = bytes;
                    entry.CompletedAt = _clock.GetTimestamp();
                    _bytes += bytes;
                }
                else Remove(key);
                entry.Completion.TrySetResult(new(snapshot, reusable));
            }
            return (result, false);
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                Remove(key);
                entry.Completion.TrySetException(error);
                _ = entry.Completion.Task.Exception; // Observe even when no callers joined this read.
            }
            throw;
        }
    }

    private void Remove(string key)
    {
        if (_entries.Remove(key, out Entry? entry)) _bytes -= entry.Bytes;
    }

    private static bool IsSuccessful(JsonElement result)
    {
        if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return false;
        if (result.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in result.EnumerateObject())
                if ((property.Name.Equals("error", StringComparison.OrdinalIgnoreCase) &&
                     property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)) ||
                    (property.Name.Equals("status", StringComparison.OrdinalIgnoreCase) &&
                     property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is "failed" or "cancelled") ||
                    (property.Name.Equals("success", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.False))
                    return false;
        }
        if (result.ValueKind != JsonValueKind.String) return true;
        string text = result.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(text) || new[] { "Source file does not exist:", "File is too large", "Search query must", "Error:", "Failed:" }
            .Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return false;
        // Some tools return JSON encoded in a string, including error objects.
        if (text.TrimStart().StartsWith('{'))
        {
            try { using JsonDocument document = JsonDocument.Parse(text); return IsSuccessful(document.RootElement); }
            catch (JsonException) { }
        }
        return true;
    }

    private sealed class Entry(TimeSpan lifetime)
    {
        internal TaskCompletionSource<CachedResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TimeSpan Lifetime { get; } = lifetime;
        internal long? CompletedAt { get; set; }
        internal int Bytes { get; set; }
    }

    private sealed record CachedResult(object? Result, bool Reusable);
}

internal sealed class CachedReadAIFunction(AIFunction inner, ToolResultCache cache,
    Func<AIFunctionArguments, string?> version, string scope, TimeSpan lifetime,
    Func<string, AIFunctionArguments, object?, CancellationToken, Task>? onHit = null) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? key = null;
        try
        {
            if (version(arguments) is string current) key = ToolResultCache.CreateKey(scope, current, arguments);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or RepositoryException or JsonException or NotSupportedException)
        {
            // A failed freshness check disables caching; the real tool still supplies its result/error.
        }
        if (key is null) return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
        var (result, hit) = await cache.InvokeAsync(key, lifetime,
            token => base.InvokeCoreAsync(arguments, token), cancellationToken).ConfigureAwait(false);
        if (hit && onHit is not null) await onHit(key, arguments, result, cancellationToken).ConfigureAwait(false);
        return result;
    }
}

internal sealed class JiraCacheInvalidatingAIFunction(AIFunction inner, ToolResultCache cache) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cache.InvalidateJira();
        try { return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false); }
        finally { cache.InvalidateJira(); }
    }
}
