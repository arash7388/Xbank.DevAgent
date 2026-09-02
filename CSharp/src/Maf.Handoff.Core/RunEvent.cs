namespace Maf.Handoff.Core;

public sealed record RunEvent(
    string Type,
    string? Agent = null,
    string? Text = null,
    string? Source = null,
    string? Target = null,
    string? Status = null,
    string? Category = null,
    TokenUsageSummary? Usage = null)
{
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public static RunEvent Delta(string? agent, string? text) => new("message", agent, text);

    public static RunEvent Handoff(string source, string target) =>
        new("handoff", Source: source, Target: target);

    public static RunEvent Finished(string status) => new("run_finished", Status: status);

    public static RunEvent Trace(string? agent, string text, string category = "lifecycle") =>
        new("trace", agent, text, Category: category);

    public static RunEvent Tool(string? agent, string text) =>
        new("tool", agent, text, Category: "tool");

    public static RunEvent Decision(string text, string status) =>
        new("decision", Text: text, Status: status, Category: "terminal");

    public static RunEvent TokenUsage(TokenUsageSummary usage) => new(
        "usage",
        Text: usage.HasUsage
            ? $"Token usage: {usage.InputTokens:N0} input, {usage.OutputTokens:N0} output, {usage.TotalTokens:N0} total." +
              (usage.EstimatedCostUsd is decimal cost ? $" Estimated cost: ${cost:F4}." : " Cost estimate unavailable: enter input and output rates for every model used.")
            : "The provider did not return token-usage metadata for this run.",
        Category: "usage",
        Usage: usage);

    public static RunEvent Error(string message) => new("error", Text: message, Status: "failed");
}
