using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Core;

public static class HandoffTopology
{
    public const string CompletionMarker = "WORKFLOW_COMPLETE";

    public const string ReviewApprovalMarker = "REVIEW_APPROVED";

    public const string RequestLimitMarker = "WORKFLOW_REQUEST_LIMIT_REACHED";

    public const string BudgetLimitMarker = "WORKFLOW_BUDGET_LIMIT_REACHED";

    public const string AgentTurnLimitMarker = "WORKFLOW_AGENT_TURN_LIMIT_REACHED";

    public const string ReviewerRemediationLimitMarker = "WORKFLOW_REVIEWER_REMEDIATION_LIMIT_REACHED";

    public const string LoopDetectedMarker = "WORKFLOW_LOOP_DETECTED";
    public const string LoopRecoveryMarker = "WORKFLOW_LOOP_RECOVERY";

    public const string ReleaseRejectionMarker = "WORKFLOW_REJECTED";

    public const string JiraClarificationMarker = "JIRA_INPUT_REQUIRED";

    public static bool IsReleaseManager(string? authorName) =>
        string.Equals(authorName, "release_manager", StringComparison.Ordinal) ||
        string.Equals(authorName, "software-delivery-release_manager", StringComparison.Ordinal);

    public static bool IsCompletionMessage(string? authorName, string? text) =>
        IsReleaseManager(authorName) &&
        text?.Contains(CompletionMarker, StringComparison.Ordinal) is true;

    public static bool IsRequestLimitMessage(string? text) =>
        text?.Contains(RequestLimitMarker, StringComparison.Ordinal) is true ||
        text?.Contains(BudgetLimitMarker, StringComparison.Ordinal) is true;

    public static bool IsReviewerRemediationLimitMessage(string? text) =>
        text?.Contains(ReviewerRemediationLimitMarker, StringComparison.Ordinal) is true;

    public static bool IsReleaseRejectionMessage(string? authorName, string? text) =>
        IsReleaseManager(authorName) &&
        text?.Contains(ReleaseRejectionMarker, StringComparison.Ordinal) is true;

    public static string? GetTerminationStatus(IReadOnlyCollection<ChatMessage> conversation, WorkflowSettings settings)
    {
        ChatMessage[] assistantMessages = [.. conversation.Where(message => message.Role == ChatRole.Assistant)];
        if (assistantMessages.Any(message =>
                (message.AuthorName is "reviewer" or "software-delivery-reviewer") &&
                !message.Contents.OfType<FunctionCallContent>().Any() &&
                message.Text.StartsWith(BuildFailureMessages.Marker, StringComparison.Ordinal)))
            return "build_blocked";
        // Limits and rejection take precedence over approval. Tool results and user-supplied
        // marker literals are evidence, not control messages.
        if (assistantMessages.Any(message => message.Text.Contains(LoopDetectedMarker, StringComparison.Ordinal)))
            return "loop_detected";
        if (assistantMessages.Any(message => message.Text.Contains(BudgetLimitMarker, StringComparison.Ordinal))) return "budget_limit_reached";
        if (assistantMessages.Any(message => IsRequestLimitMessage(message.Text))) return "request_limit_reached";
        if (assistantMessages.Any(message => message.Text.Contains(AgentTurnLimitMarker, StringComparison.Ordinal)))
            return "agent_turn_limit_reached";
        if (assistantMessages.Any(message => IsReviewerRemediationLimitMessage(message.Text)) ||
            HasExceededReviewerRemediationCycles(assistantMessages, settings.MaxReviewerRemediationCycles))
            return "reviewer_remediation_limit_reached";

        if (assistantMessages.Any(message =>
                (message.AuthorName is "jira" or "software-delivery-jira") &&
                !message.Contents.OfType<FunctionCallContent>().Any() &&
                message.Text.Contains(JiraClarificationMarker, StringComparison.Ordinal)))
            return "input_required";

        ChatMessage? release = assistantMessages.LastOrDefault(message => IsReleaseManager(message.AuthorName));
        if (release is not null && !release.Contents.OfType<FunctionCallContent>().Any())
        {
            if (IsReleaseRejectionMessage(release.AuthorName, release.Text)) return "rejected_by_release";
            if (IsCompletionMessage(release.AuthorName, release.Text))
            {
                bool reviewApproved = false;
                foreach (ChatMessage message in assistantMessages.TakeWhile(message => !ReferenceEquals(message, release)))
                {
                    if (IsDeveloper(message.AuthorName)) reviewApproved = false;
                    if (IsReviewer(message.AuthorName) && !string.IsNullOrWhiteSpace(message.Text))
                        reviewApproved = message.Text.Contains(ReviewApprovalMarker, StringComparison.Ordinal);
                }

                return reviewApproved ? "completed" : "review_approval_missing";
            }
        }

        return conversation.Count >= settings.MaxTotalMessages ? "message_limit_reached" : null;
    }

    public static bool HasExceededReviewerRemediationCycles(
        IEnumerable<ChatMessage> messages,
        int maximumCycles) =>
        HasExceededReviewerRemediationCycles(
            messages.Where(message => message.Role == ChatRole.Assistant)
                .Select(message => message.AuthorName),
            maximumCycles);

    public static bool HasExceededReviewerRemediationCycles(
        IEnumerable<string?> authorNames,
        int maximumCycles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCycles, 1);

        int cycles = 0;
        string? previousParticipant = null;
        foreach (string? authorName in authorNames)
        {
            if (!IsReviewer(authorName) && !IsDeveloper(authorName))
            {
                if (!string.IsNullOrWhiteSpace(authorName))
                {
                    previousParticipant = null;
                }

                continue;
            }

            if (IsReviewer(previousParticipant) && IsDeveloper(authorName))
            {
                cycles++;
                if (cycles > maximumCycles)
                {
                    return true;
                }
            }

            previousParticipant = authorName;
        }

        return false;
    }

    private static bool IsReviewer(string? authorName) =>
        string.Equals(authorName, "reviewer", StringComparison.Ordinal) ||
        string.Equals(authorName, "software-delivery-reviewer", StringComparison.Ordinal);

    private static bool IsDeveloper(string? authorName) =>
        string.Equals(authorName, "developer", StringComparison.Ordinal) ||
        string.Equals(authorName, "software-delivery-developer", StringComparison.Ordinal);

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Edges { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["jira"] = ["planner"],
            ["planner"] = ["developer"],
            ["developer"] = ["reviewer"],
            ["reviewer"] = ["developer", "security"],
            ["security"] = ["developer", "release_manager"],
            ["release_manager"] = [],
        };
}
