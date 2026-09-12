using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

/// <summary>
/// Works around DeepSeek.Agents.AI 1.0.4 dropping the provider's final streaming usage chunk.
/// The non-streaming mapper retains ChatResponse.Usage, so adapt that complete response back into
/// framework updates until the upstream streaming mapper accepts DeepSeek's current chunk shape.
/// </summary>
internal sealed class DeepSeekUsagePreservingChatClient(IChatClient innerClient) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        innerClient.GetResponseAsync(messages, options, cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await innerClient.GetResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : innerClient.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // The decorator does not own the provider client.
    }
}
