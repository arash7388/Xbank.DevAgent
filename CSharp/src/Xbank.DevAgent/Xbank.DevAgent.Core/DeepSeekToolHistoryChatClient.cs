using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Core;

/// <summary>
/// DeepSeek requires every tool-role message to immediately follow an assistant message containing
/// the matching tool_calls. Workflow handoffs can carry a result into a new agent without carrying
/// the originating call, so preserve that result as user-visible context instead of sending invalid
/// provider history.
/// </summary>
internal sealed class DeepSeekToolHistoryChatClient(IChatClient innerClient) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        innerClient.GetResponseAsync(Normalize(messages), options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        StreamAsync(Normalize(messages), options, cancellationToken);

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

    internal static IReadOnlyList<ChatMessage> Normalize(IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage> normalized = [];
        HashSet<string> immediatelyExpectedResults = new(StringComparer.Ordinal);

        foreach (ChatMessage message in messages)
        {
            if (message.Role == ChatRole.Tool)
            {
                FunctionResultContent[] results = [.. message.Contents.OfType<FunctionResultContent>()];
                bool allPaired = results.Length > 0 &&
                    results.All(result => immediatelyExpectedResults.Contains(result.CallId));
                if (allPaired)
                {
                    normalized.Add(message);
                }
                else
                {
                    string context = results.Length == 0
                        ? message.Text
                        : string.Join("\n", results.Select(result =>
                            $"Result from prior tool call '{result.CallId}': {result.Result}"));
                    if (!string.IsNullOrWhiteSpace(context))
                    {
                        normalized.Add(new ChatMessage(ChatRole.User, context));
                    }
                }
            }
            else
            {
                normalized.Add(message);
            }

            immediatelyExpectedResults = message.Role == ChatRole.Assistant
                ? message.Contents.OfType<FunctionCallContent>().Select(call => call.CallId).ToHashSet(StringComparer.Ordinal)
                : [];
        }

        return normalized;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : innerClient.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // The decorator does not own the provider client.
    }
}
