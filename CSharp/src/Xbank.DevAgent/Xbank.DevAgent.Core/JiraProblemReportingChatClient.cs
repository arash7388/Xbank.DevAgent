using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Core;

// Problem assessments must be reported even when the model omits the reporting tool.
internal sealed partial class JiraProblemReportingChatClient(IChatClient inner, IJiraProblemReporter reporter) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        await ReportAssessmentAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in inner.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            updates.Add(update);
        ChatResponse response = updates.ToChatResponse();
        await ReportAssessmentAsync(response, cancellationToken).ConfigureAwait(false);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    private async Task ReportAssessmentAsync(ChatResponse response, CancellationToken cancellationToken)
    {
        string? text = FindProblemAssessment(response);
        if (text is null) return;
        text = SummaryBlock().Replace(text, "").Replace(HandoffTopology.JiraClarificationMarker, "", StringComparison.Ordinal).Trim();
        await reporter.ReportProblemAsync(text, cancellationToken).ConfigureAwait(false);
    }

    internal static string? FindProblemAssessment(ChatResponse response)
    {
        FunctionCallContent[] calls = [.. response.Messages.Where(message => message.Role == ChatRole.Assistant)
            .SelectMany(message => message.Contents).OfType<FunctionCallContent>()];
        // Ordinary tools, including the explicit reporting tool, must finish before an assessment.
        if (calls.Any(call => !call.Name.StartsWith("handoff_to_", StringComparison.Ordinal))) return null;
        string text = string.Join("\n", response.Messages.Where(message => message.Role == ChatRole.Assistant).Select(message => message.Text));
        if (HasProblemMarker(text)) return text;
        string payload = string.Join("\n", calls.SelectMany(call => call.Arguments?.Values ?? []).Select(value => value?.ToString()));
        return HasProblemMarker(payload) ? payload : null;
    }

    private static bool HasProblemMarker(string text) =>
        text.Contains(HandoffTopology.JiraClarificationMarker, StringComparison.Ordinal) ||
        text.Contains("READY_WITH_ASSUMPTIONS", StringComparison.Ordinal) || text.Contains("NEEDS_CLARIFICATION", StringComparison.Ordinal);

    [GeneratedRegex(@"<agent_summary>.*?</agent_summary>", RegexOptions.Singleline)]
    private static partial Regex SummaryBlock();

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : inner.GetService(serviceType, serviceKey);
    public void Dispose() { }
}
