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
    public int MaxReviewerRemediationCycles { get; init; } = 5;

    public int MaxLlmRequests { get; init; } = 25;

    public static WorkflowSettings FromEnvironment(bool requireModel = true)
    {
        string endpoint = Environment.GetEnvironmentVariable("OPENAI_ENDPOINT")?.Trim() ??
            "https://models.github.ai/inference";
        string model = Environment.GetEnvironmentVariable("OPENAI_MODEL")?.Trim() ?? string.Empty;
        string apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")?.Trim() ?? string.Empty;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri) ||
            endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ConfigurationException("OPENAI_ENDPOINT must be an absolute HTTPS URL.");
        }

        if (requireModel && string.IsNullOrWhiteSpace(model))
        {
            throw new ConfigurationException("OPENAI_MODEL is required.");
        }

        if (requireModel && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ConfigurationException("OPENAI_API_KEY is required.");
        }

        return new(endpoint, model, apiKey, PositiveInteger("MAF_MAX_TOTAL_MESSAGES", 60),
            PositiveInteger("MAF_MAX_AGENT_TURNS", 25))
        {
            MaxReviewerRemediationCycles = PositiveInteger("MAF_MAX_REVIEWER_REMEDIATION_CYCLES", 5),
            MaxLlmRequests = PositiveInteger("MAF_MAX_LLM_REQUESTS", 25),
        };
    }

    public void Validate()
    {
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

        if (Prompt is not null && string.IsNullOrWhiteSpace(Prompt))
        {
            throw new ConfigurationException("Additional agent guidance cannot be empty.");
        }

        if (Prompt?.Length > 50_000)
        {
            throw new ConfigurationException("Additional agent guidance must be 50,000 characters or fewer.");
        }

        if (MaxReviewerRemediationCycles < 1)
        {
            throw new ConfigurationException("Maximum reviewer remediation cycles must be greater than zero.");
        }

        if (MaxLlmRequests < 1)
        {
            throw new ConfigurationException("Maximum LLM requests must be greater than zero.");
        }
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
