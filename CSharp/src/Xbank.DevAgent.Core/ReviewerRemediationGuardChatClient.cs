using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Core;

/// <summary>Shared by all agents in one workflow; counts actual agent activation transitions.</summary>
internal sealed class ReviewerRemediationBudget(int maximumCycles)
{
    private readonly Lock _gate = new();
    private string? _previousAgent;
    private int _cycles;

    public bool TryActivate(string agent)
    {
        lock (_gate)
        {
            if (_previousAgent == AgentNames.Reviewer && agent == AgentNames.Developer)
            {
                _cycles++;
            }

            _previousAgent = agent;
            return _cycles <= maximumCycles;
        }
    }

    public string StopMessage =>
        $"{HandoffTopology.ReviewerRemediationLimitMarker} The reviewer requested more than " +
        $"{maximumCycles} developer remediation cycles. The workflow stopped before another provider call. " +
        "The unresolved findings remain in the transcript; the change has not been approved.";
}

internal sealed class ReviewerRemediationGuardChatClient(
    IChatClient innerClient,
    string agent,
    ReviewerRemediationBudget budget) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return budget.TryActivate(agent)
            ? innerClient.GetResponseAsync(messages, options, cancellationToken)
            : Task.FromResult(StopResponse());
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!budget.TryActivate(agent))
        {
            foreach (ChatResponseUpdate update in StopResponse().ToChatResponseUpdates())
            {
                yield return update;
            }
            yield break;
        }

        await foreach (ChatResponseUpdate update in innerClient.GetStreamingResponseAsync(
            messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private ChatResponse StopResponse() => new(new ChatMessage(ChatRole.Assistant, budget.StopMessage)
    {
        AuthorName = agent,
    });

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : innerClient.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // The provider client is owned by the workflow.
    }
}
