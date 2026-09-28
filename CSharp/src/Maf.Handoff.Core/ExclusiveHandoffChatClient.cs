using System.Runtime.CompilerServices;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

/// <summary>
/// Prevents a provider from returning an orchestration handoff together with ordinary tool calls.
/// Microsoft Agent Framework cannot transfer the turn while those tool requests are outstanding,
/// so ordinary tools run first and the requested handoff is replayed locally on the next invocation.
/// </summary>
internal sealed class ExclusiveHandoffChatClient(IChatClient innerClient) : IChatClient
{
    private readonly Lock _pendingLock = new();
    private DeferredHandoff? _pendingHandoff;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (TryTakePendingHandoff(out DeferredHandoff? pending) && pending is not null)
        {
            return DeferredHandoffResponse(pending);
        }

        ChatResponse response = await innerClient.GetResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);
        StorePendingHandoff(RemoveHandoffsWhenOrdinaryToolsArePresent(response.Messages));
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (TryTakePendingHandoff(out DeferredHandoff? pending) && pending is not null)
        {
            foreach (ChatResponseUpdate update in DeferredHandoffResponse(pending).ToChatResponseUpdates())
            {
                yield return update;
            }

            yield break;
        }

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
        DeferredHandoff? deferred = RemoveHandoffsWhenOrdinaryToolsArePresent(response.Messages);
        StorePendingHandoff(deferred);
        IEnumerable<ChatResponseUpdate> safeUpdates = deferred is not null
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

    private static DeferredHandoff? RemoveHandoffsWhenOrdinaryToolsArePresent(
        IEnumerable<ChatMessage> messages)
    {
        ChatMessage[] materializedMessages = [.. messages];
        FunctionCallContent[] calls = GetFunctionCalls(materializedMessages);
        if (!HasMixedCalls(calls))
        {
            return null;
        }

        FunctionCallContent deferredCall = calls.First(IsHandoff);
        TextReasoningContent[] reasoning =
        [
            .. materializedMessages
                .Where(message => message.Contents.Contains(deferredCall))
                .SelectMany(message => message.Contents)
                .OfType<TextReasoningContent>()
                .Where(content => !string.IsNullOrWhiteSpace(content.Text))
                .Select(content => new TextReasoningContent(content.Text)),
        ];

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

        return new(deferredCall, reasoning);
    }

    private void StorePendingHandoff(DeferredHandoff? handoff)
    {
        if (handoff is null)
        {
            return;
        }

        lock (_pendingLock)
        {
            _pendingHandoff = handoff;
        }
    }

    private bool TryTakePendingHandoff(out DeferredHandoff? handoff)
    {
        lock (_pendingLock)
        {
            handoff = _pendingHandoff;
            _pendingHandoff = null;
            return handoff is not null;
        }
    }

    private static ChatResponse DeferredHandoffResponse(DeferredHandoff handoff) =>
        new(new ChatMessage(ChatRole.Assistant, [.. handoff.Reasoning, handoff.Call]));

    private static bool HasMixedCalls(IReadOnlyList<FunctionCallContent> calls) =>
        calls.Any(IsHandoff) && calls.Any(call => !IsHandoff(call));

    private static bool IsHandoff(FunctionCallContent call) =>
        call.Name.StartsWith(HandoffWorkflowBuilder.FunctionPrefix, StringComparison.Ordinal);

    private static FunctionCallContent[] GetFunctionCalls(IEnumerable<ChatMessage> messages) =>
        [.. messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>()];

    private sealed record DeferredHandoff(
        FunctionCallContent Call,
        IReadOnlyList<TextReasoningContent> Reasoning);
}
