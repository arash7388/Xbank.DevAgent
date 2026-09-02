namespace Maf.Handoff.Core;

public sealed record RunEvent(
    string Type,
    string? Agent = null,
    string? Text = null,
    string? Source = null,
    string? Target = null,
    string? Status = null,
    string? Category = null)
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

    public static RunEvent Error(string message) => new("error", Text: message, Status: "failed");
}
