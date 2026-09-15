using System.Runtime.CompilerServices;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

/// <summary>
/// Allows a bounded number of ordinary tool-call rounds, then requires the agent's sole handoff
/// tool. This prevents an autonomous agent from repeatedly inspecting the same source material.
/// </summary>
internal sealed class ToolRoundBudgetChatClient(
    IChatClient innerClient,
    int maximumOrdinaryToolRounds) : IChatClient
{
    private int _ordinaryToolRounds;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        (ChatOptions? effectiveOptions, string? forcedHandoff) = ConstrainOptions(options);
        ChatResponse response = await innerClient.GetResponseAsync(
            messages,
            effectiveOptions,
            cancellationToken).ConfigureAwait(false);
        EnsureHandoff(response.Messages, forcedHandoff);
        RecordOrdinaryToolRound(response.Messages.SelectMany(message => message.Contents));
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        (ChatOptions? effectiveOptions, string? forcedHandoff) = ConstrainOptions(options);
        if (forcedHandoff is not null)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in innerClient.GetStreamingResponseAsync(
                messages,
                effectiveOptions,
                cancellationToken).ConfigureAwait(false))
            {
                updates.Add(update);
            }

            ChatResponse response = updates.ToChatResponse();
            EnsureHandoff(response.Messages, forcedHandoff);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
            {
                yield return update;
            }

            yield break;
        }

        bool usedOrdinaryTool = false;
        await foreach (ChatResponseUpdate update in innerClient.GetStreamingResponseAsync(
            messages,
            effectiveOptions,
            cancellationToken).ConfigureAwait(false))
        {
            usedOrdinaryTool |= update.Contents
                .OfType<FunctionCallContent>()
                .Any(call => !IsHandoff(call));
            yield return update;
        }

        if (usedOrdinaryTool)
        {
            Interlocked.Increment(ref _ordinaryToolRounds);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : innerClient.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // This decorator does not own the provider client.
    }

    private (ChatOptions? Options, string? ForcedHandoff) ConstrainOptions(ChatOptions? options)
    {
        if (Volatile.Read(ref _ordinaryToolRounds) < maximumOrdinaryToolRounds ||
            options?.Tools is not { } tools)
        {
            return (options, null);
        }

        AITool[] handoffs = [.. tools.Where(tool =>
            tool.Name.StartsWith(HandoffWorkflowBuilder.FunctionPrefix, StringComparison.Ordinal))];
        if (handoffs.Length != 1)
        {
            return (options, null);
        }

        ChatOptions constrained = options.Clone();
        constrained.Tools = handoffs;
        // DeepSeek V4 thinking mode rejects the tool_choice parameter, including required and
        // named-tool modes. The sole handoff is instead enforced on the returned response below.
        constrained.ToolMode = null;
        constrained.Instructions = $"""
                {options.Instructions}

                TOOL ROUND BUDGET REACHED
                Repository inspection is complete. Do not request more repository tools. Produce the concise,
                repository-backed plan and acceptance criteria in the handoff payload now.
                """;
        return (constrained, handoffs[0].Name);
    }

    private static void EnsureHandoff(IEnumerable<ChatMessage> messages, string? handoffName)
    {
        if (handoffName is null)
        {
            return;
        }

        ChatMessage? assistant = messages.LastOrDefault(message => message.Role == ChatRole.Assistant);
        if (assistant is null || assistant.Contents
            .OfType<FunctionCallContent>()
            .Any(IsHandoff))
        {
            return;
        }

        for (int index = assistant.Contents.Count - 1; index >= 0; index--)
        {
            if (assistant.Contents[index] is FunctionCallContent)
            {
                assistant.Contents.RemoveAt(index);
            }
        }

        string reason = string.IsNullOrWhiteSpace(assistant.Text)
            ? "Planner inspection is complete. Implement the repository-backed plan and acceptance criteria from the preceding context."
            : assistant.Text;
        assistant.Contents.Add(new FunctionCallContent(
            Guid.NewGuid().ToString("N"),
            handoffName,
            new Dictionary<string, object?> { ["reasonForHandoff"] = reason }));
    }

    private void RecordOrdinaryToolRound(IEnumerable<AIContent> contents)
    {
        if (contents.OfType<FunctionCallContent>().Any(call => !IsHandoff(call)))
        {
            Interlocked.Increment(ref _ordinaryToolRounds);
        }
    }

    private static bool IsHandoff(FunctionCallContent call) =>
        call.Name.StartsWith(HandoffWorkflowBuilder.FunctionPrefix, StringComparison.Ordinal);
}
