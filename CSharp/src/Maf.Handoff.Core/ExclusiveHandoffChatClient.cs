using System.Runtime.CompilerServices;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

/// <summary>
/// Prevents a provider from returning an orchestration handoff together with ordinary tool calls.
/// Microsoft Agent Framework cannot transfer the turn while those tool requests are outstanding,
/// so ordinary tools run first. The model must consume their results before choosing a handoff.
/// Conflicting handoffs are blocked until the model chooses exactly one destination.
/// </summary>
internal sealed class ExclusiveHandoffChatClient(IChatClient innerClient) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await innerClient.GetResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);
        NormalizeHandoffs(response.Messages);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatResponseUpdate>? toolCallTail = null;
        await foreach (ChatResponseUpdate update in innerClient.GetStreamingResponseAsync(
            messages,
            options,
            cancellationToken).ConfigureAwait(false))
        {
            if (toolCallTail is null &&
                !update.Contents.OfType<FunctionCallContent>().Any())
            {
                yield return update;
                continue;
            }

            (toolCallTail ??= []).Add(update);
        }

        if (toolCallTail is null)
        {
            yield break;
        }

        ChatResponse response = toolCallTail.ToChatResponse();
        bool removed = NormalizeHandoffs(response.Messages);
        IEnumerable<ChatResponseUpdate> safeUpdates = removed
            ? response.ToChatResponseUpdates()
            : toolCallTail;
        foreach (ChatResponseUpdate update in safeUpdates)
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this)
            ? this
            : innerClient.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // This decorator does not own the provider client, which is shared by workflow agents.
    }

    private static bool NormalizeHandoffs(
        IEnumerable<ChatMessage> messages)
    {
        ChatMessage[] materializedMessages = [.. messages];
        FunctionCallContent[] calls = GetFunctionCalls(materializedMessages);
        bool mixed = HasMixedCalls(calls);
        bool ambiguous = calls.Count(IsHandoff) > 1;
        if (!mixed && !ambiguous)
        {
            return false;
        }

        foreach (ChatMessage message in materializedMessages)
        {
            for (int index = message.Contents.Count - 1; index >= 0; index--)
            {
                if (message.Contents[index] is FunctionCallContent call && IsHandoff(call))
                {
                    message.Contents.RemoveAt(index);
                }
            }
        }

        if (ambiguous && !mixed)
        {
            materializedMessages.Last(message => message.Role == ChatRole.Assistant).Contents.Add(
                new TextContent("Handoff blocked: choose exactly one handoff destination. " +
                    "Decide whether the current findings require remediation or allow the next gate, " +
                    "then request only that handoff."));
        }

        return true;
    }

    private static bool HasMixedCalls(IReadOnlyList<FunctionCallContent> calls) =>
        calls.Any(IsHandoff) && calls.Any(call => !IsHandoff(call));

    private static bool IsHandoff(FunctionCallContent call) =>
        call.Name.StartsWith(HandoffWorkflowBuilder.FunctionPrefix, StringComparison.Ordinal);

    private static FunctionCallContent[] GetFunctionCalls(IEnumerable<ChatMessage> messages) =>
        [.. messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>()];

}
