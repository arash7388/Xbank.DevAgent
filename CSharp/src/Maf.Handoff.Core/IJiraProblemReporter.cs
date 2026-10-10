namespace Maf.Handoff.Core;

public sealed record JiraProblemReport(string? IssueKey, string Status, string Message, string? CommentId = null, string? Url = null);

// This capability is bound to the run's task; callers cannot choose another issue to modify.
public interface IJiraProblemReporter
{
    Task<JiraProblemReport> ReportProblemAsync(string problemInPersian, CancellationToken cancellationToken = default);
}
