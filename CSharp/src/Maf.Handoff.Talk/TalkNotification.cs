using Maf.Handoff.Core;

namespace Maf.Handoff.Talk;

public sealed record TalkNotification(Guid RunId, string Step, string Status, string? Details = null)
{
    public string Format(bool includeDetails)
    {
        string text = $"MAF | {Step} | {Status}\nRun: {RunId:D}";
        if (includeDetails && !string.IsNullOrWhiteSpace(Details))
        {
            int length = Math.Min(Details.Length, 2000);
            if (char.IsHighSurrogate(Details[length - 1])) length--;
            text += "\n" + Details[..length];
        }
        return text;
    }

    public static TalkNotification? FromEvent(Guid runId, RunEvent runEvent, bool notifyAgentSteps = true) => runEvent switch
    {
        { Type: "handoff", Source: not null } when notifyAgentSteps =>
            new(runId, runEvent.Source, "step_finished", $"Handed off to {runEvent.Target}."),
        { Type: "clarification_requested" } => new(runId, "workflow", "awaiting_input", runEvent.Text),
        _ => null,
    };

    public static TalkNotification? FromSession(Guid runId, string status, string? details) => status switch
    {
        "committed" or "pushed" when !string.IsNullOrWhiteSpace(details) =>
            new(runId, "pull_request", "failed", details),
        "awaiting_approval" or "completed" or "no_changes" or "committed" or "pushed" or
        "pull_request_created" or "failed" or "approval_failed" or "cancelled" or "rejected" =>
            new(runId, "workflow", status, details),
        _ => null,
    };
}
