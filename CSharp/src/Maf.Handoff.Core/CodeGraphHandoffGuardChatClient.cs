using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

internal sealed class CodeGraphHandoffGuardChatClient(
    IChatClient innerClient,
    string agentName,
    CodeGraphAudit audit) : IChatClient
{
    private const string BlockedHandoffMessage =
        "Handoff blocked by protected CodeGraph policy: inspect the relevant C# symbol relations with " +
        "get_context, get_context_by_id, or get_neighbors, then retry the handoff with concrete evidence.";

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await innerClient.GetResponseAsync(
            messages,
            options,
            cancellationToken).ConfigureAwait(false);
        return ShouldBlock(GetFunctionCalls(response.Messages))
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant, BlockedHandoffMessage))
            : response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in innerClient.GetStreamingResponseAsync(
            messages,
            options,
            cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
        }

        if (ShouldBlock(GetFunctionCalls(updates.ToChatResponse().Messages)))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, BlockedHandoffMessage);
            yield break;
        }

        foreach (ChatResponseUpdate update in updates)
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

    private bool ShouldBlock(IReadOnlyList<FunctionCallContent> calls) =>
        calls.Count > 0 &&
        calls.All(call => call.Name.StartsWith("handoff_to_", StringComparison.Ordinal)) &&
        !audit.TryConsumeRelationshipCheck(agentName);

    private static FunctionCallContent[] GetFunctionCalls(IEnumerable<ChatMessage> messages) =>
        [.. messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>()];
}
