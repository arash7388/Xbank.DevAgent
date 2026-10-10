namespace Maf.Handoff.Core;

public interface IWorkflowUserInput
{
    bool IsAvailable => true;
    Task<string> AskAsync(string questions, CancellationToken cancellationToken = default);
}

public interface IJiraScopeChangeReporter
{
    Task<JiraProblemReport> ReportScopeChangeAsync(string changesInPersian, CancellationToken cancellationToken = default);
}
