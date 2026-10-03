using System.Runtime.CompilerServices;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

/// <summary>
/// Removes ordinary tool call/result pairs from completed handoff turns. Keep all evidence in
/// the current turn: requesting another tool does not mean earlier results can be forgotten.
/// The workflow keeps its complete transcript; only the history sent to the model is projected.
/// </summary>
internal sealed class CompletedToolCycleFilteringChatClient(IChatClient innerClient) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        innerClient.GetResponseAsync(Filter(messages), options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        StreamAsync(Filter(messages), options, cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (ChatResponseUpdate update in innerClient.GetStreamingResponseAsync(
            messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    internal static IReadOnlyList<ChatMessage> Filter(IEnumerable<ChatMessage> messages)
    {
        ChatMessage[] history = [.. messages];
        Dictionary<string, FunctionCallContent> ordinaryCalls = new(StringComparer.Ordinal);
        foreach (ChatMessage message in history)
        {
            foreach (FunctionCallContent call in message.Contents.OfType<FunctionCallContent>())
            {
                if (!IsHandoff(call) && !string.IsNullOrWhiteSpace(call.CallId))
                {
                    ordinaryCalls[call.CallId] = call;
                }
            }
        }

        HashSet<string> consumedCallIds = new(StringComparer.Ordinal);
        bool hasLaterHandoff = false;
        for (int index = history.Length - 1; index >= 0; index--)
        {
            ChatMessage message = history[index];
            if (hasLaterHandoff)
            {
                foreach (FunctionResultContent result in message.Contents.OfType<FunctionResultContent>())
                {
                    if (ordinaryCalls.ContainsKey(result.CallId))
                    {
                        consumedCallIds.Add(result.CallId);
                    }
                }
            }

            if (message.Role == ChatRole.Assistant &&
                message.Contents.OfType<FunctionCallContent>().Any(IsHandoff))
            {
                hasLaterHandoff = true;
            }
        }

        if (consumedCallIds.Count == 0)
        {
            return history;
        }

        List<ChatMessage> filtered = new(history.Length);
        foreach (ChatMessage message in history)
        {
            List<AIContent> contents = [.. message.Contents.Where(content => content switch
            {
                FunctionCallContent call => !consumedCallIds.Contains(call.CallId),
                FunctionResultContent result => !consumedCallIds.Contains(result.CallId),
                _ => true,
            })];

            if (contents.Count == 0)
            {
                continue;
            }

            if (contents.Count == message.Contents.Count)
            {
                filtered.Add(message);
                continue;
            }

            ChatMessage projected = message.Clone();
            projected.Contents = contents;
            filtered.Add(projected);
        }

        return filtered;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : innerClient.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // This decorator does not own the provider client.
    }

    private static bool IsHandoff(FunctionCallContent call) =>
        call.Name.StartsWith(HandoffWorkflowBuilder.FunctionPrefix, StringComparison.Ordinal);
}
