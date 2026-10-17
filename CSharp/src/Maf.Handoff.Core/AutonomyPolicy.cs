namespace Maf.Handoff.Core;

public static class AutonomyPolicy
{
    public static bool IsValid(int level) => level is 0 or 25 or 50 or 75 or 100;

    public static string Instructions(int level) => "\n\nRUN AUTONOMY POLICY (takes precedence over role guidance about asking the user): " + (level switch
    {
        0 => "0% Guided. Ask the user through ask_user_decision before choosing a major approach or making significant changes. Make routine implementation choices yourself after the approach is confirmed.",
        25 => "25% Assisted. Make routine implementation decisions yourself. Ask through ask_user_decision when requirements are unclear or a major approach needs a decision.",
        50 => "50% Collaborative. Resolve minor ambiguity yourself and record assumptions. Ask through ask_user_decision only when the decision significantly changes scope, requirements, or expected behavior.",
        75 => "75% Highly autonomous. Choose the approach and resolve ambiguity using repository and task evidence. Record assumptions and impact. Ask through ask_user_decision only for an essential decision that cannot reasonably be inferred.",
        100 => "100% Fully autonomous. Never wait for human clarification or approval. Choose reasonable assumptions from task and repository evidence, record the decisions and impact, and continue implementation, independent review, and remediation. If a clarification tool returns delegated decision guidance, decide yourself; it is not a factual answer from the user.",
        _ => throw new ConfigurationException("Autonomy must be 0, 25, 50, 75, or 100."),
    }) + " Use ask_user_decision when available. Preserve independent review, truthful verification, cancellation, execution limits, and the configured build policy. Fix review findings through the developer/reviewer cycle. Publishing is controlled separately by the run's upfront selections; do not infer publishing authorization from autonomy. Never invent credentials or claim unavailable checks passed.";

    public const string DelegatedDecision = "The user selected 100% autonomy and delegated this decision to the agents. This is policy guidance, not a user-provided factual clarification. Resolve the question using available task and repository evidence, choose and document reasonable assumptions, and continue without asking the user. Do not fabricate unavailable facts or verification results.";
}

/// <summary>Prevents clarification tools from pausing a fully autonomous run.</summary>
public sealed class AutonomyUserInput(int level, IWorkflowUserInput inner, Action<string>? onDecision = null) : IWorkflowUserInput
{
    public bool IsAvailable => level == 100 || inner.IsAvailable;

    public Task<string> AskAsync(string questions, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (level != 100) return inner.AskAsync(questions, cancellationToken);
        onDecision?.Invoke(questions);
        return Task.FromResult(AutonomyPolicy.DelegatedDecision);
    }
}
