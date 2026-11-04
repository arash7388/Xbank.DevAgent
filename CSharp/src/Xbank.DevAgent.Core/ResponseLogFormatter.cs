using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Core;

internal static partial class ResponseLogFormatter
{
    internal static string FormatToolRequest(FunctionCallContent call, string identity)
    {
        object? path = null;
        if (call.Name == "read_repository_file") call.Arguments?.TryGetValue("path", out path);
        string? file = path switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
        string detail = string.IsNullOrWhiteSpace(file) ? "" : $" — {Whitespace().Replace(file, " ").Trim()}";
        return $"Requested tool: {call.Name}{detail} (call {identity}).";
    }

    internal static string Format(IEnumerable<ChatMessage> messages, string? finishReason = null)
    {
        ChatMessage[] response = [.. messages];
        FunctionCallContent[] calls = [.. response.SelectMany(message => message.Contents).OfType<FunctionCallContent>()];
        FunctionResultContent[] results = [.. response.SelectMany(message => message.Contents).OfType<FunctionResultContent>()];
        List<string> details = [];

        string? reply = response.LastOrDefault(message =>
            message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.Text))?.Text;
        if (reply is not null) details.Add($"Reply: {Preview(reply, 400)}");

        foreach (IGrouping<string, FunctionCallContent> tool in calls
            .Where(call => !call.Name.StartsWith("handoff_to_", StringComparison.Ordinal))
            .GroupBy(call => call.Name, StringComparer.Ordinal).Take(8))
        {
            HashSet<string> ids = tool.Select(call => call.CallId).ToHashSet(StringComparer.Ordinal);
            FunctionResultContent[] returned = [.. results.Where(result => ids.Contains(result.CallId))];
            string? resultText = returned.LastOrDefault()?.Result switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null,
            };
            details.Add($"Tool {tool.Key}: {tool.Count()} requested, {returned.Length} results returned" +
                (string.IsNullOrWhiteSpace(resultText) ? "." : $"; latest result: {Preview(resultText, 160)}"));
        }

        foreach (FunctionCallContent handoff in calls.Where(call =>
            call.Name.StartsWith("handoff_to_", StringComparison.Ordinal)).Take(4))
        {
            object? reason = null;
            handoff.Arguments?.TryGetValue("reasonForHandoff", out reason);
            string? reasonText = reason switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null,
            };
            details.Add($"Handoff requested to {handoff.Name["handoff_to_".Length..]}" +
                (string.IsNullOrWhiteSpace(reasonText) ? "." : $": {Preview(reasonText, 240)}"));
        }

        if (details.Count == 0) details.Add("No assistant text or tool calls were returned.");
        if (!string.IsNullOrWhiteSpace(finishReason)) details.Add($"Finish reason: {finishReason}.");
        return "Response completed. " + string.Join(" ", details);
    }

    private static string Preview(string text, int limit)
    {
        string preview = Whitespace().Replace(text.Replace("<agent_summary>", "", StringComparison.Ordinal)
            .Replace("</agent_summary>", "", StringComparison.Ordinal), " ").Trim();
        if (preview.Length <= limit) return preview;
        int end = char.IsHighSurrogate(preview[limit - 1]) ? limit - 1 : limit;
        return preview[..end].TrimEnd() + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
