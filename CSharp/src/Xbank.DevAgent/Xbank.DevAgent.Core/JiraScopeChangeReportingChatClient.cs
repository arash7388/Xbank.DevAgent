using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Core;

internal sealed partial class JiraScopeChangeReportingChatClient(IChatClient inner, IJiraScopeChangeReporter reporter) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        await ReportAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in inner.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            updates.Add(update);
        ChatResponse response = updates.ToChatResponse();
        await ReportAsync(response, cancellationToken).ConfigureAwait(false);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    private async Task ReportAsync(ChatResponse response, CancellationToken cancellationToken)
    {
        string? changes = FindChanges(response);
        if (changes is null) return;
        JiraProblemReport result = await reporter.ReportScopeChangeAsync(changes, cancellationToken).ConfigureAwait(false);
        response.Messages.Insert(0, new ChatMessage(ChatRole.Assistant, $"Jira scope-change comment status: {result.Status}\n{result.Message}")
            { AuthorName = AgentNames.Developer });
    }

    internal static string? FindChanges(ChatResponse response)
    {
        ChatMessage[] messages = [.. response.Messages.Where(message => message.Role == ChatRole.Assistant)];
        FunctionCallContent[] calls = [.. messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>()];
        if (calls.Any(call => !call.Name.StartsWith("handoff_to_", StringComparison.Ordinal))) return null;
        string text = string.Join("\n", messages.Select(message => message.Text).Concat(
            calls.SelectMany(call => call.Arguments?.Values ?? []).Select(value => value?.ToString())));
        string changes = string.Join("\n\n", ScopeBlock().Matches(text).Select(match => match.Groups[1].Value.Trim())
            .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal));
        return changes.Length == 0 ? null : changes;
    }

    [GeneratedRegex(@"<jira_scope_change>(.*?)</jira_scope_change>", RegexOptions.Singleline)]
    private static partial Regex ScopeBlock();

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : inner.GetService(serviceType, serviceKey);
    public void Dispose() { }
}
