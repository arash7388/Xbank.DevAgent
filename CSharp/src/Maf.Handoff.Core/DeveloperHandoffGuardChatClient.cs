using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

internal sealed class DeveloperHandoffGuardChatClient(
    IChatClient innerClient,
    Func<bool> hasRepositoryChanges) : IChatClient
{
    private const string BlockedHandoffMessage =
        "Developer handoff blocked: the repository diff is empty. Inspect the repository, make the " +
        "requested edit with a repository write tool, then verify Git status and the exact diff before " +
        "handing off to reviewer.";

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await innerClient.GetResponseAsync(
            messages,
            options,
            cancellationToken).ConfigureAwait(false);
        FunctionCallContent[] calls = GetFunctionCalls(response.Messages);
        if (ShouldBlock(calls))
        {
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, BlockedHandoffMessage));
        }

        return response;
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

        ChatResponse response = updates.ToChatResponse();
        FunctionCallContent[] calls = GetFunctionCalls(response.Messages);
        if (ShouldBlock(calls))
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
        // This decorator does not own the provider client, which is shared by all workflow agents.
    }

    private bool ShouldBlock(IReadOnlyList<FunctionCallContent> calls) =>
        !hasRepositoryChanges() &&
        calls.Count > 0 &&
        calls.All(call => call.Name.StartsWith("handoff_to_", StringComparison.Ordinal));

    private static FunctionCallContent[] GetFunctionCalls(IEnumerable<ChatMessage> messages) =>
        [.. messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>()];

}
