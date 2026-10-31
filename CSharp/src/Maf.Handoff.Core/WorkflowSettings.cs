namespace Maf.Handoff.Core;

public sealed record WorkflowSettings(
    string ModelEndpoint,
    string Model,
    string ApiKey,
    int MaxTotalMessages = 60,
    int MaxAgentTurns = 25,
    ModelProviderDefinition? Provider = null,
    string? Prompt = null)
{
    public AifaRequestContext? AifaContext { get; init; }

    public bool SkipBuildVerification { get; init; }

    public bool IsMergeRequestReview { get; init; }

    public bool ReleaseManagerValidationEnabled { get; init; }

    public bool JiraValidationEnabled { get; init; } = true;

    public int AutonomyLevel { get; init; } = 100;

    public int MaxReviewerRemediationCycles { get; init; } = 5;

    public int MaxLlmRequests { get; init; } = 25;

    public int MaxRepeatedWorkflowStates { get; init; } = 3;

    public decimal? MaxBudgetUsd { get; init; }

    public static WorkflowSettings FromEnvironment(bool requireModel = true)
    {
        string endpoint = Environment.GetEnvironmentVariable("OPENAI_ENDPOINT")?.Trim() ??
            "https://models.github.ai/inference";
        string model = Environment.GetEnvironmentVariable("OPENAI_MODEL")?.Trim() ?? string.Empty;
        string apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")?.Trim() ?? string.Empty;
        bool useAifa = string.Equals(Environment.GetEnvironmentVariable("MAF_MODEL_PROVIDER")?.Trim(),
            "aifa", StringComparison.OrdinalIgnoreCase);
        if (useAifa)
        {
            endpoint = AifaRequestContext.Endpoint;
            model = Environment.GetEnvironmentVariable("AIFA_MODEL")?.Trim() ?? AifaRequestContext.Model;
            apiKey = Environment.GetEnvironmentVariable("AIFA_API_KEY")?.Trim() ?? string.Empty;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri) ||
            endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ConfigurationException("OPENAI_ENDPOINT must be an absolute HTTPS URL.");
        }

        if (requireModel && string.IsNullOrWhiteSpace(model))
        {
            throw new ConfigurationException(useAifa ? "AIFA_MODEL is required." : "OPENAI_MODEL is required.");
        }

        if (requireModel && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ConfigurationException(useAifa ? "AIFA_API_KEY is required." : "OPENAI_API_KEY is required.");
        }

        ModelProviderDefinition? provider = useAifa ? new(model, "AIFA", endpoint,
            ToolCalling: true, Vision: false, MaxInputTokens: 8192, MaxOutputTokens: 2048,
            Thinking: true, ReasoningEffort: "low", SupportsReasoningEffort: ["low", "medium", "high", "xhigh", "max"]) : null;
        return new(endpoint, model, apiKey, PositiveInteger("MAF_MAX_TOTAL_MESSAGES", 60),
            PositiveInteger("MAF_MAX_AGENT_TURNS", 25), Provider: provider)
        {
            AifaContext = useAifa ? new AifaRequestContext(
                Environment.GetEnvironmentVariable("AIFA_USER_ID")?.Trim() ?? "123",
                Environment.GetEnvironmentVariable("AIFA_SESSION_ID")?.Trim() ?? Guid.NewGuid().ToString("N"),
                Environment.GetEnvironmentVariable("AIFA_REQUEST_ID")?.Trim()) : null,
            MaxReviewerRemediationCycles = PositiveInteger("MAF_MAX_REVIEWER_REMEDIATION_CYCLES", 5),
            MaxLlmRequests = PositiveInteger("MAF_MAX_LLM_REQUESTS", 25),
            MaxRepeatedWorkflowStates = PositiveInteger("MAF_MAX_REPEATED_WORKFLOW_STATES", 3),
        };
    }

    public void Validate()
    {
        if (!AutonomyPolicy.IsValid(AutonomyLevel))
            throw new ConfigurationException("Autonomy must be 0, 25, 50, 75, or 100.");
        if (!Uri.TryCreate(ModelEndpoint, UriKind.Absolute, out Uri? endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ConfigurationException("Model endpoint must be an absolute HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(Model) || string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ConfigurationException("Model and model API key are required.");
        }

        Provider?.NormalizeAndValidate();
        if (AifaRequestContext.IsAifa(endpoint))
        {
            if (AifaContext is null) throw new ConfigurationException("AIFA request context is required.");
            AifaContext.Validate();
        }

        if (Prompt is not null && string.IsNullOrWhiteSpace(Prompt))
        {
            throw new ConfigurationException("Additional agent guidance cannot be empty.");
        }

        if (Prompt?.Length > 50_000)
        {
            throw new ConfigurationException("Additional agent guidance must be 50,000 characters or fewer.");
        }

        if (MaxTotalMessages < 1 || MaxAgentTurns < 1)
        {
            throw new ConfigurationException("Maximum total messages and agent turns must be greater than zero.");
        }

        if (MaxReviewerRemediationCycles < 1)
        {
            throw new ConfigurationException("Maximum reviewer remediation cycles must be greater than zero.");
        }

        if (MaxBudgetUsd is <= 0)
        {
            throw new ConfigurationException("Maximum budget in USD must be greater than zero.");
        }

        if (MaxLlmRequests < 1)
        {
            throw new ConfigurationException("Maximum LLM requests must be greater than zero.");
        }

        if (MaxRepeatedWorkflowStates < 2)
            throw new ConfigurationException("Maximum repeated workflow states must be at least two.");
    }

    private static int PositiveInteger(string name, int defaultValue)
    {
        string raw = Environment.GetEnvironmentVariable(name) ??
            defaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!int.TryParse(raw, out int value) || value < 1)
        {
            throw new ConfigurationException($"{name} must be an integer greater than zero.");
        }

        return value;
    }
}

public sealed class ConfigurationException(string message) : Exception(message);
