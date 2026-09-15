namespace Maf.Handoff.Core;

public sealed record AgentModelConfiguration(
    ModelProviderDefinition Provider,
    string ApiKey,
    string? Prompt = null)
{
    public WorkflowSettings ToSettings(
        int maxTotalMessages,
        int maxAgentTurns,
        int maxReviewerRemediationCycles = 5,
        int maxLlmRequests = 20)
    {
        if (Provider is null)
        {
            throw new ConfigurationException("Provider definition is required.");
        }

        ModelProviderDefinition provider = Provider.NormalizeAndValidate();
        WorkflowSettings settings = new(
            provider.Url,
            provider.Id,
            ApiKey?.Trim() ?? string.Empty,
            maxTotalMessages,
            maxAgentTurns,
            provider,
            string.IsNullOrWhiteSpace(Prompt) ? null : Prompt.Trim())
        {
            MaxReviewerRemediationCycles = maxReviewerRemediationCycles,
            MaxLlmRequests = maxLlmRequests,
        };
        settings.Validate();
        return settings;
    }
}

public static class AgentNames
{
    public const string Jira = "jira";
    public const string Planner = "planner";
    public const string Developer = "developer";
    public const string Reviewer = "reviewer";
    public const string Security = "security";
    public const string ReleaseManager = "release_manager";

    public static IReadOnlyList<string> All { get; } =
        [Jira, Planner, Developer, Reviewer, Security, ReleaseManager];

    // Planner defines the implementation contract, Developer is the only role allowed to change
    // the repository, and ReleaseManager is the terminal approval gate.
    public static IReadOnlyList<string> Required { get; } =
        [Planner, Developer, ReleaseManager];

    public static IReadOnlyList<string> Optional { get; } =
        [Jira, Reviewer, Security];
}
