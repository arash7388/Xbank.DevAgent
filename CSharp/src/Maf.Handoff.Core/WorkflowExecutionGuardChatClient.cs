using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

// Counts model turns outside checkpointing and tool invocation. Framework autonomous-turn
// counters alone do not cover tool rounds or repeated handoff activations.
internal sealed class WorkflowExecutionBudget(WorkflowSettings settings)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _turns = new(StringComparer.Ordinal);
    private int _requests;
    private string? _stopMessage;

    public string? Reserve(string agent)
    {
        lock (_gate)
        {
            if (_stopMessage is not null) return _stopMessage;
            if (_requests >= settings.MaxLlmRequests)
            {
                return _stopMessage = $"{HandoffTopology.RequestLimitMarker} The workflow reached " +
                    $"its limit of {settings.MaxLlmRequests} model requests, including checkpoint replays.";
            }

            int turns = _turns.GetValueOrDefault(agent);
            if (turns >= settings.MaxAgentTurns)
            {
                return _stopMessage = $"{HandoffTopology.AgentTurnLimitMarker} Agent '{agent}' reached " +
                    $"its limit of {settings.MaxAgentTurns} model turns across all activations and tool rounds.";
            }

            _requests++;
            _turns[agent] = turns + 1;
            return null;
        }
    }
}

internal sealed class WorkflowExecutionGuardChatClient(
    IChatClient innerClient, string agent, WorkflowExecutionBudget budget) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? stop = budget.Reserve(agent);
        return stop is null
            ? innerClient.GetResponseAsync(messages, options, cancellationToken)
            : Task.FromResult(StopResponse(stop));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? stop = budget.Reserve(agent);
        if (stop is not null)
        {
            foreach (ChatResponseUpdate update in StopResponse(stop).ToChatResponseUpdates())
                yield return update;
            yield break;
        }

        await foreach (ChatResponseUpdate update in innerClient.GetStreamingResponseAsync(
            messages, options, cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    private ChatResponse StopResponse(string text) => new(new ChatMessage(ChatRole.Assistant, text)
    {
        AuthorName = agent,
    });

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : innerClient.GetService(serviceType, serviceKey);

    public void Dispose() { } // The workflow owns the provider.
}
