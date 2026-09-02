using Maf.Handoff.Core;

namespace Maf.Handoff.Tests;

public sealed class WorkflowSettingsTests
{
    [Fact]
    public void DefaultsToFiveReviewerRemediationCycles()
    {
        WorkflowSettings settings = new("https://example.test", "model", "key");

        Assert.Equal(5, settings.MaxReviewerRemediationCycles);
    }

    [Fact]
    public void FromEnvironmentRequiresModel()
    {
        using EnvironmentVariableScope model = new("OPENAI_MODEL", null);
        using EnvironmentVariableScope apiKey = new("OPENAI_API_KEY", null);

        ConfigurationException exception = Assert.Throws<ConfigurationException>(
            () => WorkflowSettings.FromEnvironment());

        Assert.Contains("OPENAI_MODEL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromEnvironmentRejectsInvalidTurnLimit()
    {
        using EnvironmentVariableScope limit = new("MAF_MAX_AGENT_TURNS", "0");

        ConfigurationException exception = Assert.Throws<ConfigurationException>(
            () => WorkflowSettings.FromEnvironment(requireModel: false));

        Assert.Contains("greater than zero", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNoReviewerRemediationCycles()
    {
        WorkflowSettings settings = new("https://example.test", "model", "key")
        {
            MaxReviewerRemediationCycles = 0,
        };

        ConfigurationException exception = Assert.Throws<ConfigurationException>(settings.Validate);

        Assert.Contains("reviewer remediation", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    internal EnvironmentVariableScope(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}
