namespace Maf.Handoff.Core;

public sealed record AgentModelConfiguration(
    ModelProviderDefinition Provider,
    string ApiKey,
    string? Prompt = null)
{
    public WorkflowSettings ToSettings(
        int maxTotalMessages,
        int maxAgentTurns,
        int maxReviewerRemediationCycles = 5)
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
            Prompt?.Trim())
        {
            MaxReviewerRemediationCycles = maxReviewerRemediationCycles,
        };
        settings.Validate();
        return settings;
    }
}

public static class AgentNames
{
    public const string Intake = "intake";
    public const string Planner = "planner";
    public const string Developer = "developer";
    public const string Reviewer = "reviewer";
    public const string Security = "security";
    public const string ReleaseManager = "release_manager";

    public static IReadOnlyList<string> All { get; } =
        [Intake, Planner, Developer, Reviewer, Security, ReleaseManager];

    // Intake begins every run, Developer is the only role allowed to change the repository,
    // and ReleaseManager is the terminal approval gate. The stages between them can be omitted.
    public static IReadOnlyList<string> Required { get; } =
        [Intake, Developer, ReleaseManager];

    public static IReadOnlyList<string> Optional { get; } =
        [Planner, Reviewer, Security];
}
