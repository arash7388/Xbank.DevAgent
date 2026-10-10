using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

/// <summary>Runs an actual build before the reviewer's forward handoff can execute.</summary>
internal sealed class ReviewerBuildGuardChatClient(
    IChatClient innerClient,
    Func<CancellationToken, Task<RepositoryBuildResult>> build) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatResponse response = await innerClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        await CheckAsync(response, options, cancellationToken).ConfigureAwait(false);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in innerClient.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            updates.Add(update);
        ChatResponse response = updates.ToChatResponse();
        await CheckAsync(response, options, cancellationToken).ConfigureAwait(false);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    private async Task CheckAsync(ChatResponse response, ChatOptions? options, CancellationToken cancellationToken)
    {
        // HandoffWorkflowFactory registers reviewer remediation first and the forward gate second.
        AITool[] handoffs = options?.Tools?.Where(tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal)).ToArray() ?? [];
        FunctionCallContent[] calls = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
        if (handoffs.Length != 2 || calls.Length != 1 || calls[0].Name != handoffs[1].Name) return;

        RepositoryBuildResult result = await build(cancellationToken).ConfigureAwait(false);
        string evidence = result.Succeeded
            ? result.Evidence
            : "BUILD_FAILED. Reviewer must inspect these diagnostics; Developer must fix the build failure, " +
              "then return to Reviewer for independent review and another build. Do not claim approval.\n" + result.Evidence;
        FunctionCallContent call = calls[0];
        Dictionary<string, object?> arguments = call.Arguments is null ? [] : new(call.Arguments);
        string prior = arguments.GetValueOrDefault("reasonForHandoff")?.ToString() ?? "";
        arguments["reasonForHandoff"] = result.Succeeded ? prior + "\n" + evidence : evidence;
        foreach (ChatMessage message in response.Messages)
        {
            for (int index = 0; index < message.Contents.Count; index++)
            {
                if (ReferenceEquals(message.Contents[index], call))
                    message.Contents[index] = new FunctionCallContent(call.CallId,
                        result.Succeeded ? call.Name : handoffs[0].Name, arguments);
                else if (!result.Succeeded && message.Contents[index] is TextContent text)
                    message.Contents[index] = new TextContent(text.Text.Replace(HandoffTopology.ReviewApprovalMarker, "REVIEW_PENDING_BUILD", StringComparison.Ordinal));
            }
        }
        response.Messages.Last().Contents.Add(new TextContent(evidence));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : innerClient.GetService(serviceType, serviceKey);

    public void Dispose() { }
}
