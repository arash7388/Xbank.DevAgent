using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

// Older/custom prompts may emit a terminal marker instead of using the clarification tool.
// Keep that assessment inside this activation until the user has answered it.
internal sealed class JiraClarificationChatClient(IChatClient inner, IWorkflowUserInput input) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => ContinueAsync(messages, options, false, cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await ContinueAsync(messages, options, true, cancellationToken).ConfigureAwait(false);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    private async Task<ChatResponse> ContinueAsync(IEnumerable<ChatMessage> messages, ChatOptions? options,
        bool streaming, CancellationToken cancellationToken)
    {
        List<ChatMessage> history = [.. messages];
        List<ChatMessage> clarifications = [];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChatResponse response;
            if (streaming)
            {
                List<ChatResponseUpdate> updates = [];
                await foreach (ChatResponseUpdate update in inner.GetStreamingResponseAsync(history, options, cancellationToken).ConfigureAwait(false))
                    updates.Add(update);
                response = updates.ToChatResponse();
            }
            else response = await inner.GetResponseAsync(history, options, cancellationToken).ConfigureAwait(false);

            string? assessment = JiraProblemReportingChatClient.FindProblemAssessment(response);
            // Reporting includes non-blocking assumptions; only an explicit blocking
            // assessment should interrupt a ready handoff and wait for user input.
            if (assessment is null ||
                (!assessment.Contains(HandoffTopology.JiraClarificationMarker, StringComparison.Ordinal) &&
                 !assessment.Contains("NEEDS_CLARIFICATION", StringComparison.Ordinal)))
            {
                // Downstream agents also need the user's answers as task evidence.
                foreach (ChatMessage message in clarifications.AsEnumerable().Reverse()) response.Messages.Insert(0, message);
                return response;
            }

            string questions = assessment.Replace(HandoffTopology.JiraClarificationMarker, "", StringComparison.Ordinal).Trim();
            string answer = await input.AskAsync(questions, cancellationToken).ConfigureAwait(false);
            // Discard a premature handoff: it was never executed and has no tool result.
            ChatMessage question = new(ChatRole.Assistant, questions) { AuthorName = AgentNames.Jira };
            ChatMessage reply = new(ChatRole.User, (answer == AutonomyPolicy.DelegatedDecision
                ? "Run autonomy policy guidance (no human answer was requested):\n"
                : "User clarification for this Jira task:\n") + answer);
            history.Add(question);
            history.Add(reply);
            clarifications.Add(question);
            clarifications.Add(reply);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : inner.GetService(serviceType, serviceKey);
    public void Dispose() { }
}
