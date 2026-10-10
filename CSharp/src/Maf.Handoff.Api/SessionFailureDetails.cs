using System.Text;
using Maf.Handoff.Core;

namespace Maf.Handoff.Api;

public sealed record SessionFailureDetails(string Title, string Summary, string? Status, string? Agent, string Message)
{
    public static SessionFailureDetails? FromEvents(string? error, IReadOnlyList<RunEvent> events)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;

        string? status = events.LastOrDefault(item => item.Type == "run_finished")?.Status;
        string title = status switch
        {
            "input_required" => "Clarification needed",
            "rejected_by_release" => "Release blocked",
            "loop_detected" => "Workflow loop detected",
            "request_limit_reached" or "agent_turn_limit_reached" or "budget_limit_reached" or
                "message_limit_reached" or "reviewer_remediation_limit_reached" => "Workflow limit reached",
            _ => "Workflow stopped",
        };

        // Persisted events contain individual streaming fragments; live events may already be merged.
        // Reconstruct complete messages without mixing authors or separate tool rounds.
        List<(string? Agent, string Text)> messages = [];
        StringBuilder text = new();
        string? agent = null;
        foreach (RunEvent item in events)
        {
            if (item.Type != "message" || (text.Length > 0 && item.Agent != agent)) Flush();
            if (item.Type == "message")
            {
                agent = item.Agent;
                text.Append(item.Text);
            }
        }
        Flush();

        var source = status switch
        {
            "loop_detected" => messages.LastOrDefault(item =>
                item.Text.Contains(HandoffTopology.LoopDetectedMarker, StringComparison.Ordinal)),
            "input_required" => messages.LastOrDefault(item =>
                (item.Agent is "jira" or "software-delivery-jira") &&
                item.Text.Contains(HandoffTopology.JiraClarificationMarker, StringComparison.Ordinal)),
            "rejected_by_release" or "review_approval_missing" =>
                messages.LastOrDefault(item => HandoffTopology.IsReleaseManager(item.Agent)),
            _ => default,
        };
        if (status == "input_required" && string.IsNullOrWhiteSpace(source.Text))
            source = messages.LastOrDefault();

        return new(title, error, status, source.Agent,
            string.IsNullOrWhiteSpace(source.Text) ? error : source.Text);

        void Flush()
        {
            if (text.Length == 0) return;
            messages.Add((agent, text.ToString()));
            text.Clear();
        }
    }
}
