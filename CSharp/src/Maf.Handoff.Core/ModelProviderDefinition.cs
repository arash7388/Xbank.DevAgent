namespace Maf.Handoff.Core;

public sealed record ModelProviderDefinition(
    string Id,
    string Name,
    string Url,
    bool ToolCalling,
    bool Vision,
    int MaxInputTokens,
    int MaxOutputTokens,
    bool Thinking,
    string? ReasoningEffort,
    IReadOnlyList<string>? SupportsReasoningEffort,
    decimal? InputCostPerMillionTokens = null,
    decimal? OutputCostPerMillionTokens = null)
{
    private const int MaximumDeclaredTokens = 2_000_000;

    public ModelProviderDefinition NormalizeAndValidate()
    {
        string id = Id?.Trim() ?? string.Empty;
        string name = Name?.Trim() ?? string.Empty;
        string reasoningEffort = ReasoningEffort?.Trim().ToLowerInvariant() ?? string.Empty;
        string[] supportedEfforts = (SupportsReasoningEffort ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            throw new ConfigurationException("Provider id and name are required.");
        }

        if (!ToolCalling)
        {
            throw new ConfigurationException("The selected provider must support tool calling for agent handoffs.");
        }

        ValidateTokenLimit(MaxInputTokens, nameof(MaxInputTokens));
        ValidateTokenLimit(MaxOutputTokens, nameof(MaxOutputTokens));
        ValidateTokenPrice(InputCostPerMillionTokens, nameof(InputCostPerMillionTokens));
        ValidateTokenPrice(OutputCostPerMillionTokens, nameof(OutputCostPerMillionTokens));

        if (!Thinking && !string.IsNullOrEmpty(reasoningEffort))
        {
            throw new ConfigurationException("reasoningEffort requires thinking to be enabled.");
        }

        if (Thinking && !string.IsNullOrEmpty(reasoningEffort) &&
            !supportedEfforts.Contains(reasoningEffort, StringComparer.Ordinal))
        {
            throw new ConfigurationException("reasoningEffort must be listed in supportsReasoningEffort.");
        }

        return this with
        {
            Id = id,
            Name = name,
            Url = NormalizeEndpoint(Url),
            ReasoningEffort = string.IsNullOrEmpty(reasoningEffort) ? null : reasoningEffort,
            SupportsReasoningEffort = supportedEfforts,
        };
    }

    public static string NormalizeEndpoint(string rawUrl)
    {
        string value = rawUrl?.Trim() ?? string.Empty;
        int markdownSeparator = value.IndexOf("](", StringComparison.Ordinal);
        if (value.StartsWith("[", StringComparison.Ordinal) && markdownSeparator > 1 &&
            value.EndsWith(")", StringComparison.Ordinal))
        {
            value = value[(markdownSeparator + 2)..^1].Trim();
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ConfigurationException("Provider url must be an absolute HTTPS URL without credentials, query, or fragment.");
        }

        const string chatCompletionsPath = "/chat/completions";
        string path = endpoint.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(chatCompletionsPath, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^chatCompletionsPath.Length];
        }

        UriBuilder builder = new(endpoint)
        {
            Path = string.IsNullOrEmpty(path) ? "/" : path,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static void ValidateTokenLimit(int value, string name)
    {
        if (value < 1 || value > MaximumDeclaredTokens)
        {
            throw new ConfigurationException($"Provider {name} must be between 1 and {MaximumDeclaredTokens:N0}.");
        }
    }

    private static void ValidateTokenPrice(decimal? value, string name)
    {
        if (value is < 0 or > 1_000_000)
        {
            throw new ConfigurationException($"Provider {name} must be between 0 and 1,000,000 USD per million tokens.");
        }
    }
}
