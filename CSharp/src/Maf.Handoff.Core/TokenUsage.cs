using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

/// <summary>Provider-reported token totals for an agent during one workflow run.</summary>
public sealed record AgentTokenUsage(
    string Agent,
    string Model,
    long LlmRequestCount,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long TotalTokens,
    decimal? EstimatedCostUsd,
    long CheckpointHits = 0);

/// <summary>Provider-reported token totals and an optional rate-based cost estimate for a workflow run.</summary>
public sealed record TokenUsageSummary(
    IReadOnlyList<AgentTokenUsage> ByAgent,
    long LlmRequestCount,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long TotalTokens,
    decimal? EstimatedCostUsd)
{
    public long CheckpointHits => ByAgent.Sum(item => item.CheckpointHits);

    public TokenUsageSummary WithCheckpoints(IReadOnlyList<CheckpointHitLogEntry> hits)
    {
        List<AgentTokenUsage> agents = ByAgent.Select(item => item with { CheckpointHits = 0 }).ToList();
        foreach (var group in hits.GroupBy(hit => (hit.Agent, hit.Model)))
        {
            int index = agents.FindIndex(item => item.Agent == group.Key.Agent && item.Model == group.Key.Model);
            if (index >= 0) agents[index] = agents[index] with { CheckpointHits = group.Count() };
            else agents.Add(new(group.Key.Agent, group.Key.Model, 0, 0, 0, 0, 0, 0, 0m, group.Count()));
        }
        string[] order = [.. AgentNames.All];
        return this with { ByAgent = agents.OrderBy(item => Array.IndexOf(order, item.Agent)).ToArray() };
    }

    public bool HasUsage => TotalTokens > 0;

    public bool HasRequests => LlmRequestCount > 0;

    public static TokenUsageSummary Empty { get; } = new([], 0, 0, 0, 0, 0, 0, null);
}

/// <summary>
/// Collects token usage returned by providers. It deliberately does not estimate tokens when a provider omits
/// usage metadata: displayed counts are always provider-reported counts.
/// </summary>
public sealed class TokenUsageCollector(
    IReadOnlyDictionary<string, WorkflowSettings> settingsByAgent,
    int maxLlmRequests = int.MaxValue,
    decimal? maxBudgetUsd = null)
{
    private readonly ConcurrentDictionary<string, MutableAgentUsage> _usage = new(StringComparer.Ordinal);
    private readonly Lock _requestLock = new();
    private int _requestCount;
    private bool _budgetExceeded;

    public void RecordCheckpointHit(string agent)
    {
        WorkflowSettings settings = GetSettings(agent);
        _usage.GetOrAdd(agent, _ => new(settings.Model, settings.Provider, settings.ModelEndpoint)).AddCheckpointHit();
    }

    /// <summary>Reserves one outbound provider request, returning false when the run budget is exhausted.</summary>
    public bool RecordRequest(string agent)
    {
        if (string.IsNullOrWhiteSpace(agent))
        {
            return true;
        }

        lock (_requestLock)
        {
            if (maxBudgetUsd is decimal budget &&
                Snapshot().ByAgent.Sum(item => item.EstimatedCostUsd ?? 0m) >= budget)
            {
                _budgetExceeded = true;
                return false;
            }

            if (_requestCount >= maxLlmRequests)
            {
                return false;
            }

            _requestCount++;
            WorkflowSettings settings = GetSettings(agent);
            _usage.GetOrAdd(agent, _ => new(settings.Model, settings.Provider, settings.ModelEndpoint)).AddRequest();
            return true;
        }
    }

    public string RequestLimitMessage(string agent) => _budgetExceeded
        ? $"{HandoffTopology.BudgetLimitMarker} The workflow reached its estimated budget of ${maxBudgetUsd:0.00} USD. " +
          $"The next request would have been made by '{agent}'."
        :
        $"{HandoffTopology.RequestLimitMarker} The workflow reached its limit of {maxLlmRequests:N0} " +
        $"AI requests before release approval. The next request would have been made by '{agent}'.";

    public void Record(string agent, UsageDetails? usage)
    {
        if (usage is null || string.IsNullOrWhiteSpace(agent))
        {
            return;
        }

        long? reportedInput = usage.InputTokenCount;
        long? reportedOutput = usage.OutputTokenCount;
        long input = reportedInput ?? 0;
        long cachedInput = Math.Clamp(usage.CachedInputTokenCount ?? 0, 0, input);
        long output = reportedOutput ?? 0;
        long reasoning = Math.Clamp(usage.ReasoningTokenCount ?? 0, 0, output);
        long total = usage.TotalTokenCount ?? input + output;
        if (input == 0 && output == 0 && total == 0)
        {
            return;
        }

        WorkflowSettings settings = GetSettings(agent);
        _usage.GetOrAdd(agent, _ => new(settings.Model, settings.Provider, settings.ModelEndpoint)).Add(
            input,
            cachedInput,
            output,
            reasoning,
            total,
            reportedInput.HasValue && reportedOutput.HasValue);
    }

    public TokenUsageSummary Snapshot()
    {
        string[] orderedAgents = [.. AgentNames.All];
        AgentTokenUsage[] byAgent = _usage
            .OrderBy(pair => Array.IndexOf(orderedAgents, pair.Key))
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value.Snapshot(pair.Key))
            .ToArray();
        long requests = byAgent.Sum(item => item.LlmRequestCount);
        long input = byAgent.Sum(item => item.InputTokens);
        long cachedInput = byAgent.Sum(item => item.CachedInputTokens);
        long output = byAgent.Sum(item => item.OutputTokens);
        long reasoning = byAgent.Sum(item => item.ReasoningTokens);
        long total = byAgent.Sum(item => item.TotalTokens);
        decimal? cost = byAgent.All(item => item.EstimatedCostUsd.HasValue)
            ? byAgent.Sum(item => item.EstimatedCostUsd ?? 0m)
            : null;
        return new(byAgent, requests, input, cachedInput, output, reasoning, total, cost);
    }

    private WorkflowSettings GetSettings(string agent) => settingsByAgent.TryGetValue(agent, out WorkflowSettings? value)
        ? value
        : throw new ConfigurationException($"Model configuration for agent '{agent}' is required.");

    private sealed class MutableAgentUsage(string model, ModelProviderDefinition? provider, string endpoint)
    {
        private readonly bool _isAifa = AifaRequestContext.IsAifa(new Uri(endpoint));
        private readonly Lock _lock = new();
        private long _requests;
        private long _checkpointHits;
        private long _input;
        private long _cachedInput;
        private long _output;
        private long _reasoning;
        private long _total;
        private bool _hasCompletePricingBreakdown = true;
        private bool _hasUsage;

        internal void AddRequest()
        {
            lock (_lock)
            {
                _requests++;
            }
        }

        internal void AddCheckpointHit()
        {
            lock (_lock) { _checkpointHits++; }
        }

        internal void Add(long input, long cachedInput, long output, long reasoning, long total, bool hasCompletePricingBreakdown)
        {
            lock (_lock)
            {
                _input += input;
                _cachedInput += cachedInput;
                _output += output;
                _reasoning += reasoning;
                _total += total;
                _hasUsage = true;
                _hasCompletePricingBreakdown &= hasCompletePricingBreakdown;
            }
        }

        internal AgentTokenUsage Snapshot(string agent)
        {
            lock (_lock)
            {
                decimal? cost = _hasUsage && _hasCompletePricingBreakdown &&
                    provider?.InputCostPerMillionTokens is decimal inputRate &&
                    provider.OutputCostPerMillionTokens is decimal outputRate
                    ? ((_input - _cachedInput) / 1_000_000m * inputRate) +
                      (_cachedInput / 1_000_000m * (provider.CachedInputCostPerMillionTokens ?? inputRate)) +
                      (_output / 1_000_000m * outputRate)
                    : null;
                if (_isAifa || (_requests == 0 && _checkpointHits > 0)) cost = 0m;
                return new(agent, model, _requests, _input, _cachedInput, _output, _reasoning, _total, cost, _checkpointHits);
            }
        }
    }
}

internal sealed class UsageTrackingChatClient(
    IChatClient innerClient,
    string agent,
    TokenUsageCollector usageCollector) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!usageCollector.RecordRequest(agent))
        {
            return RequestLimitResponse();
        }

        ChatResponse response = await innerClient.GetResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);
        usageCollector.Record(agent, GetUsage(response));
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!usageCollector.RecordRequest(agent))
        {
            foreach (ChatResponseUpdate update in RequestLimitResponse().ToChatResponseUpdates())
            {
                yield return update;
            }

            yield break;
        }

        UsageDetails? usage = null;
        await foreach (ChatResponseUpdate update in innerClient.GetStreamingResponseAsync(
            messages,
            options,
            cancellationToken).ConfigureAwait(false))
        {
            usage = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? usage;
            yield return update;
        }

        usageCollector.Record(agent, usage);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : innerClient.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // The decorator does not own the provider client.
    }

    private static UsageDetails? GetUsage(ChatResponse response) =>
        response.Usage ?? response.Messages
            .SelectMany(message => message.Contents)
            .OfType<UsageContent>()
            .LastOrDefault()
            ?.Details;

    private ChatResponse RequestLimitResponse() =>
        new(new ChatMessage(ChatRole.Assistant, usageCollector.RequestLimitMessage(agent)));
}
