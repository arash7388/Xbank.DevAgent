using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

/// <summary>
/// Works around DeepSeek.Agents.AI 1.0.4 dropping the provider's final streaming usage chunk.
/// The non-streaming mapper retains ChatResponse.Usage, so adapt that complete response back into
/// framework updates until the upstream streaming mapper accepts DeepSeek's current chunk shape.
/// </summary>
internal sealed class DeepSeekUsagePreservingChatClient(
    IChatClient innerClient, DeepSeekUsageCapture? usageCapture = null, IDisposable? ownedTransport = null) : IChatClient
{
    private readonly ConcurrentDictionary<string, string> _reasoningByCallId = new(StringComparer.Ordinal);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        DeepSeekUsageCapture.CallUsage? call = usageCapture?.BeginCall();
        ChatResponse response = await innerClient.GetResponseAsync(
            RestoreReasoning(messages), options, cancellationToken).ConfigureAwait(false);
        if (call?.Usage is { } usage) response.Usage = usage;
        if (call?.RawUsage is { } rawUsage)
        {
            response.AdditionalProperties ??= [];
            response.AdditionalProperties["provider_usage"] = rawUsage;
        }
        response.ResponseId ??= call?.ResponseId;
        RememberReasoning(response.Messages);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    private IReadOnlyList<ChatMessage> RestoreReasoning(IEnumerable<ChatMessage> messages)
    {
        ChatMessage[] history = [.. messages];
        for (int index = 0; index < history.Length; index++)
        {
            ChatMessage message = history[index];
            if (message.Role != ChatRole.Assistant || HasReasoning(message))
            {
                continue;
            }

            string? reasoning = message.Contents
                .OfType<FunctionCallContent>()
                .Select(call => _reasoningByCallId.GetValueOrDefault(call.CallId))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (reasoning is null)
            {
                continue;
            }

            ChatMessage restored = message.Clone();
            restored.Contents = [new TextReasoningContent(reasoning), .. message.Contents];
            restored.AdditionalProperties ??= [];
            restored.AdditionalProperties["reasoning_content"] = reasoning;
            history[index] = restored;
        }

        return history;
    }

    private void RememberReasoning(IEnumerable<ChatMessage> messages)
    {
        foreach (ChatMessage message in messages.Where(message => message.Role == ChatRole.Assistant))
        {
            string? reasoning = GetReasoning(message);
            if (string.IsNullOrWhiteSpace(reasoning))
            {
                continue;
            }

            foreach (FunctionCallContent call in message.Contents.OfType<FunctionCallContent>())
            {
                if (!string.IsNullOrWhiteSpace(call.CallId))
                {
                    _reasoningByCallId[call.CallId] = reasoning;
                }
            }
        }
    }

    private static bool HasReasoning(ChatMessage message) =>
        !string.IsNullOrWhiteSpace(GetReasoning(message));

    private static string? GetReasoning(ChatMessage message)
    {
        if (message.AdditionalProperties?.TryGetValue("reasoning_content", out object? value) == true &&
            value is string metadataReasoning &&
            !string.IsNullOrWhiteSpace(metadataReasoning))
        {
            return metadataReasoning;
        }

        return message.Contents
            .OfType<TextReasoningContent>()
            .FirstOrDefault(content => !string.IsNullOrWhiteSpace(content.Text))
            ?.Text;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : innerClient.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // The decorator does not own the provider client.
        ownedTransport?.Dispose();
    }
}
