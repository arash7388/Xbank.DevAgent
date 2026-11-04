using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Core;

/// <summary>Records explicit forward-handoff approval in the reviewer transcript.</summary>
internal sealed class ReviewerApprovalChatClient(IChatClient innerClient) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatResponse response = await innerClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        RecordApproval(response, options);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in innerClient.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            updates.Add(update);
        ChatResponse response = updates.ToChatResponse();
        RecordApproval(response, options);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    private static void RecordApproval(ChatResponse response, ChatOptions? options)
    {
        // Change workflows register remediation then the forward gate; MR reviews have only the forward gate.
        AITool[] handoffs = options?.Tools?.Where(tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal)).ToArray() ?? [];
        FunctionCallContent[] calls = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
        if (handoffs.Length is not (1 or 2) || calls.Length != 1 || calls[0].Name != handoffs[^1].Name) return;
        string? reason = calls[0].Arguments?.TryGetValue("reasonForHandoff", out object? value) is true
            ? value?.ToString() : null;
        if (reason?.Contains(HandoffTopology.ReviewApprovalMarker, StringComparison.Ordinal) is not true) return;

        // Keep approval on the final reviewer message so appended evidence cannot hide it.
        ChatMessage? last = response.Messages.LastOrDefault(message => message.Role == ChatRole.Assistant);
        if (last is not null && !last.Text.Contains(HandoffTopology.ReviewApprovalMarker, StringComparison.Ordinal))
            last.Contents.Add(new TextContent("\n" + HandoffTopology.ReviewApprovalMarker));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : innerClient.GetService(serviceType, serviceKey);

    public void Dispose() { }
}
