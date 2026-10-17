using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

// Capture the agents' own summaries without another model call or changing workflow decisions.
internal sealed partial class AgentRunSummaryCollector(IEnumerable<string> agents)
{
    private readonly string[] _agents = [.. agents];
    private string? _activeAgent;

    internal IEnumerable<RunEvent> InitialEvents() => _agents.Select(agent =>
        RunEvent.AgentSummary(agent, "در این اجرا نوبت به این عامل نرسید.", "not_run"));

    internal RunEvent? Activate(string? agent)
    {
        string? name = Normalize(agent);
        if (name is null || name == _activeAgent) return null;
        _activeAgent = name;
        return RunEvent.AgentSummary(name, "این عامل پیش از توقف، خلاصه‌ای ارائه نکرد.", "unavailable");
    }

    internal RunEvent? Observe(string? agent, IEnumerable<ChatMessage> messages)
    {
        string? name = Normalize(agent);
        if (name is null) return null;
        string? summary = null;
        foreach (ChatMessage message in messages.Where(message => message.Role == ChatRole.Assistant))
        {
            // Tool results and messages attributed to another participant cannot supply this summary.
            if (message.AuthorName is not null && Normalize(message.AuthorName) != name) continue;
            string? ownSummary = Extract(message.Text);
            summary = ownSummary ?? summary;
            if (ownSummary is not null) continue;
            foreach (FunctionCallContent call in message.Contents.OfType<FunctionCallContent>()
                .Where(call => call.Name.StartsWith("handoff_to_", StringComparison.Ordinal)))
            {
                foreach (object? value in call.Arguments?.Values ?? [])
                {
                    string? payload = value switch
                    {
                        string text => text,
                        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                        _ => null,
                    };
                    summary = Extract(payload) ?? summary;
                }
            }
        }

        return summary is null ? null : RunEvent.AgentSummary(name, summary);
    }

    private string? Normalize(string? agent)
    {
        const string prefix = "software-delivery-";
        string? name = agent?.StartsWith(prefix, StringComparison.Ordinal) is true ? agent[prefix.Length..] : agent;
        return name is not null && _agents.Contains(name, StringComparer.Ordinal) ? name : null;
    }

    private static string? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        MatchCollection matches = SummaryBlock().Matches(text);
        foreach (Match match in matches.Reverse())
        {
            string summary = Whitespace().Replace(match.Groups[1].Value, " ").Trim();
            if (summary.Length == 0) continue;
            if (summary.Length > 600)
            {
                int end = char.IsHighSurrogate(summary[598]) ? 598 : 599;
                summary = summary[..end].TrimEnd() + "…";
            }
            return summary;
        }
        return null;
    }

    [GeneratedRegex(@"<agent_summary>\s*(.*?)\s*</agent_summary>", RegexOptions.Singleline)]
    private static partial Regex SummaryBlock();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
