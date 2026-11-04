using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Core;

// Lives inside the request budget and outside the provider; checkpoint replays
// never pass through this client and therefore never incur provider usage twice.
internal sealed class DurableUsageChatClient(
    IChatClient inner, IAiCallStore store, string runId, string agent, WorkflowSettings settings) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatMessage[] input = [.. messages];
        string id = store.StartAiCall(runId, agent, settings,
            CheckpointSerialization.CreateRequest(agent, settings, input, options).InputJson, false);
        ChatResponse response;
        try { response = await inner.GetResponseAsync(input, options, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) { store.FinishAiCall(id, null, null, error); throw; }
        store.FinishAiCall(id, response, response.Usage ?? response.Messages.SelectMany(m => m.Contents)
            .OfType<UsageContent>().LastOrDefault()?.Details, null);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatMessage[] input = [.. messages];
        string id = store.StartAiCall(runId, agent, settings,
            CheckpointSerialization.CreateRequest(agent, settings, input, options).InputJson, true);
        List<ChatResponseUpdate> updates = [];
        bool finished = false;
        UsageDetails? usage = null;
        try
        {
            await using IAsyncEnumerator<ChatResponseUpdate> enumerator = inner.GetStreamingResponseAsync(input, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool next;
                try { next = await enumerator.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception error)
                {
                    store.FinishAiCall(id, updates.Count > 0 ? updates.ToChatResponse() : null, usage, error);
                    finished = true;
                    throw;
                }
                if (!next) break;
                ChatResponseUpdate update = enumerator.Current;
                usage = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? usage;
                updates.Add(update);
                yield return update;
            }
            ChatResponse response = updates.ToChatResponse();
            store.FinishAiCall(id, response, response.Usage ?? usage, null);
            finished = true;
        }
        finally
        {
            if (!finished) store.FinishAiCall(id, updates.Count > 0 ? updates.ToChatResponse() : null, usage,
                new OperationCanceledException("The response stream was cancelled or disposed before completion."));
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : inner.GetService(serviceType, serviceKey);
    public void Dispose() { }
}
