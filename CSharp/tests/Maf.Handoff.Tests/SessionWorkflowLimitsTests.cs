using System.Reflection;
using System.Text.Json;
using Maf.Handoff.Api;
using Maf.Handoff.Core;
using Microsoft.Extensions.Configuration;

namespace Maf.Handoff.Tests;

public sealed class SessionWorkflowLimitsTests
{
    [Theory]
    [InlineData(null, 60)]
    [InlineData("90", 90)]
    public void SavedLimitsMatchConfiguredExecutionLimit(string? configured, int expected)
    {
        RepositorySessionService service = Create(configured);
        CreateSessionRequest request = new("https://example.test/repo.git", "main", "TASK-42", new Dictionary<string, AgentModelConfiguration>(), null,
            MaxAiCalls: 80);
        string json = (string)typeof(RepositorySessionService).GetMethod("SerializeWorkflowConfig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [request])!;
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement limits = document.RootElement.GetProperty("limits");
        Assert.Equal(expected, limits.GetProperty("maxAgentTurns").GetInt32());
        Assert.Equal(100, limits.GetProperty("maxTotalMessages").GetInt32());
        Assert.Equal(80, limits.GetProperty("maxLlmRequests").GetInt32());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void NonpositiveTurnLimitIsRejected(string configured)
    {
        var exception = Assert.Throws<TargetInvocationException>(() => typeof(RepositorySessionService)
            .GetProperty("MaxAgentTurns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Create(configured)));
        Assert.IsType<ConfigurationException>(exception.InnerException);
    }

    private static RepositorySessionService Create(string? configured) => new(null!, null!, null!, null!, null!, null!, null!, null!,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Workflow:MaxAgentTurns"] = configured }).Build());
}
