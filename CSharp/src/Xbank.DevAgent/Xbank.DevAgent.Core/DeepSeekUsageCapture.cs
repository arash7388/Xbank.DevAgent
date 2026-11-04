using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Core;

// The native adapter discards cache and reasoning usage. Capture only the usage
// object before its response mapper runs, with a separate holder for each call.
internal sealed class DeepSeekUsageCapture : DelegatingHandler
{
    private readonly AsyncLocal<CallUsage?> _current = new();

    internal CallUsage BeginCall() => _current.Value = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && _current.Value is { } call)
        {
            // This client intentionally requests non-streaming responses.
            byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);
            call.ResponseId = document.RootElement.TryGetProperty("id", out JsonElement id) ? id.GetString() : null;
            if (document.RootElement.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
            {
                call.Usage = ParseUsage(usage);
                call.RawUsage = usage.Clone();
            }
        }
        return response;
    }

    internal static UsageDetails ParseUsage(JsonElement usage)
    {
        static long? Count(JsonElement value, string name) =>
            value.TryGetProperty(name, out JsonElement count) && count.TryGetInt64(out long number) ? number : null;

        long? hit = Count(usage, "prompt_cache_hit_tokens");
        long? miss = Count(usage, "prompt_cache_miss_tokens");
        UsageDetails result = new()
        {
            InputTokenCount = Count(usage, "prompt_tokens") ?? (hit.HasValue && miss.HasValue ? hit + miss : null),
            CachedInputTokenCount = hit,
            OutputTokenCount = Count(usage, "completion_tokens"),
            TotalTokenCount = Count(usage, "total_tokens"),
            ReasoningTokenCount = usage.TryGetProperty("completion_tokens_details", out JsonElement details) && details.ValueKind == JsonValueKind.Object
                ? Count(details, "reasoning_tokens") : null,
        };
        if (hit.HasValue || miss.HasValue)
        {
            result.AdditionalCounts = [];
            if (hit is long hitCount) result.AdditionalCounts["prompt_cache_hit_tokens"] = hitCount;
            if (miss is long missCount) result.AdditionalCounts["prompt_cache_miss_tokens"] = missCount;
        }
        return result;
    }

    internal sealed class CallUsage
    {
        internal UsageDetails? Usage { get; set; }
        internal JsonElement? RawUsage { get; set; }
        internal string? ResponseId { get; set; }
    }
}
