namespace Maf.Handoff.Core;

public static class HandoffTopology
{
    public const string CompletionMarker = "WORKFLOW_COMPLETE";

    public static bool IsReleaseManager(string? authorName) =>
        string.Equals(authorName, "release_manager", StringComparison.Ordinal) ||
        string.Equals(authorName, "software-delivery-release_manager", StringComparison.Ordinal);

    public static bool IsCompletionMessage(string? authorName, string? text) =>
        IsReleaseManager(authorName) &&
        text?.Contains(CompletionMarker, StringComparison.Ordinal) is true;

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
            ["intake"] = ["planner"],
            ["planner"] = ["developer"],
            ["developer"] = ["reviewer"],
            ["reviewer"] = ["developer", "security"],
            ["security"] = ["developer", "release_manager"],
            ["release_manager"] = ["planner", "developer"],
        };
}
