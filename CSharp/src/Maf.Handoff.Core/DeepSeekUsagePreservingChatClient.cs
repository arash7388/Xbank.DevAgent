using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
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
    private readonly ConcurrentDictionary<string, string> _reasoningByMessage = new(StringComparer.Ordinal);

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
            if (message.Role != ChatRole.Assistant)
            {
                continue;
            }

            string? reasoning = GetReasoning(message) ?? message.Contents
                .OfType<FunctionCallContent>()
                .Select(call => _reasoningByCallId.GetValueOrDefault(call.CallId))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (reasoning is null && message.MessageId is { } messageId)
                reasoning = _reasoningByMessage.GetValueOrDefault("id:" + messageId);
            if (reasoning is null && TextKey(message) is { } textKey)
                reasoning = _reasoningByMessage.GetValueOrDefault(textKey);
            if (string.IsNullOrWhiteSpace(reasoning))
            {
                continue;
            }

            ChatMessage restored = message.Clone();
            restored.Contents = [new TextReasoningContent(reasoning),
                .. message.Contents.Where(content => content is not TextReasoningContent)];
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

            message.MessageId ??= Guid.NewGuid().ToString("N");
            _reasoningByMessage["id:" + message.MessageId] = reasoning;
            if (TextKey(message) is { } textKey)
            {
                // When IDs are stripped, restore only unambiguous text matches.
                _reasoningByMessage.AddOrUpdate(textKey, reasoning,
                    (_, previous) => previous == reasoning ? previous : string.Empty);
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

    private static string? TextKey(ChatMessage message) =>
        !string.IsNullOrWhiteSpace(message.Text) && !message.Contents.OfType<FunctionCallContent>().Any()
            ? "text:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.Text)))
            : null;

    private static string? GetReasoning(ChatMessage message)
    {
        if (message.AdditionalProperties?.TryGetValue("reasoning_content", out object? value) == true &&
            value is string metadataReasoning &&
            !string.IsNullOrWhiteSpace(metadataReasoning))
        {
            return metadataReasoning;
        }

        string reasoning = string.Concat(message.Contents.OfType<TextReasoningContent>().Select(content => content.Text));
        return string.IsNullOrWhiteSpace(reasoning) ? null : reasoning;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : innerClient.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // The decorator does not own the provider client.
        ownedTransport?.Dispose();
    }
}
