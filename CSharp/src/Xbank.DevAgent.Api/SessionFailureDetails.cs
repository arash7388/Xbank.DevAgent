using System.Text;
using Xbank.DevAgent.Core;

namespace Xbank.DevAgent.Api;

public sealed record SessionFailureDetails(string Title, string Summary, string? Status, string? Agent, string Message,
    string? TechnicalDetails = null)
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

        // Also explain archived runs that hit the old build/review loop. Use actual
        // build diagnostics rather than an agent's speculation about the failure.
        string? buildEvidence = messages.LastOrDefault(item =>
            (item.Agent is "reviewer" or "software-delivery-reviewer") &&
            (item.Text.Contains("BUILD_FAILED.", StringComparison.Ordinal) ||
             item.Text.StartsWith(BuildFailureMessages.Marker, StringComparison.Ordinal))).Text;
        if (status == "build_blocked" || (status == "loop_detected" && buildEvidence is not null))
        {
            string? explanation = BuildFailureMessages.DescribeEnvironmentFailure(buildEvidence ?? "");
            if (status == "build_blocked")
            {
                string? assessment = buildEvidence?.Split("\n\nBuild diagnostics:", 2, StringSplitOptions.None)[0];
                explanation ??= assessment?.Replace(BuildFailureMessages.Marker, "", StringComparison.Ordinal).Trim();
            }
            if (!string.IsNullOrWhiteSpace(explanation))
                return new("Build verification blocked", "Build verification could not complete.", "build_blocked", null,
                    explanation, buildEvidence);
        }

        if (status == "loop_detected")
            return new("Run stopped without progress", "The proposed changes are preserved and have not been approved.",
                status, null,
                "The agents repeated the same work without resolving the remaining issue. Review the last build or review findings in the activity log before starting a new run.",
                messages.LastOrDefault(item => item.Text.Contains(HandoffTopology.LoopDetectedMarker, StringComparison.Ordinal)).Text ?? error);

        if (status == "agent_turn_limit_reached")
        {
            var limit = messages.LastOrDefault(item => item.Text.Contains(HandoffTopology.AgentTurnLimitMarker, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(limit.Text))
                return new(title, error, status, limit.Agent,
                    limit.Text.Replace(HandoffTopology.AgentTurnLimitMarker, "", StringComparison.Ordinal).Trim(), limit.Text);
        }

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
