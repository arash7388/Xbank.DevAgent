using System.Reflection;
using System.Text.Json;
using Xbank.DevAgent.Api;
using Xbank.DevAgent.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class SessionWorkflowLimitsTests
{
    [Theory]
    [InlineData("simple", 1000, 30, 60)]
    [InlineData("normal", 2000, 60, 120)]
    [InlineData("hard", 6000, 180, 360)]
    public void ComplexityScalesServerDefaultsAndPreservesExplicitLimits(string complexity, int messages, int turns, int developerTurns)
    {
        AgentModelConfiguration profile = new(new ModelProviderDefinition("model", "Test", "https://example.test",
            true, false, 8192, 2048, false, null, null), "key");
        CreateSessionRequest request = new("https://example.test/repo.git", "main", "TASK-42",
            new Dictionary<string, AgentModelConfiguration>
            {
                [AgentNames.Planner] = profile,
                [AgentNames.Developer] = profile,
                [AgentNames.Reviewer] = profile with { MaxAgentTurns = 47 },
            }, null, MaxAiCalls: 123, MaxBudgetUsd: 0.23m, TaskComplexity: complexity);
        string json = (string)typeof(RepositorySessionService).GetMethod("SerializeWorkflowConfig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Create(null), [request])!;
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(complexity, root.GetProperty("TaskComplexity").GetString());
        Assert.Equal(messages, root.GetProperty("limits").GetProperty("maxTotalMessages").GetInt32());
        Assert.Equal(123, root.GetProperty("limits").GetProperty("maxLlmRequests").GetInt32());
        Assert.Equal(0.23m, root.GetProperty("limits").GetProperty("maxBudgetUsd").GetDecimal());
        JsonElement agents = root.GetProperty("agents");
        Assert.Equal(turns, agents.GetProperty("planner").GetProperty("maxAgentTurns").GetInt32());
        Assert.Equal(developerTurns, agents.GetProperty("developer").GetProperty("maxAgentTurns").GetInt32());
        Assert.Equal(47, agents.GetProperty("reviewer").GetProperty("maxAgentTurns").GetInt32());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void InvalidComplexityIsRejected(string? complexity)
    {
        CreateSessionRequest request = new("https://example.test/repo.git", "main", "TASK-42",
            new Dictionary<string, AgentModelConfiguration>(), null, TaskComplexity: complexity!);
        var exception = Assert.Throws<TargetInvocationException>(() => typeof(RepositorySessionService)
            .GetMethod("Validate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Create(null), [request]));
        Assert.IsType<ArgumentException>(exception.InnerException);
        Assert.Contains("Task complexity", exception.InnerException.Message);
    }

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
        Assert.Equal(2000, limits.GetProperty("maxTotalMessages").GetInt32());
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

    [Theory]
    [InlineData(null, 2000)]
    [InlineData("4000", 4000)]
    [InlineData("100", 100)]
    public void MessageLimitIsConfigurableAndSaved(string? configured, int expected)
    {
        RepositorySessionService service = new(null!, null!, null!, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Workflow:MaxTotalMessages"] = configured }).Build());
        int executionLimit = (int)typeof(RepositorySessionService)
            .GetProperty("MaxTotalMessages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        CreateSessionRequest request = new("https://example.test/repo.git", "main", "TASK-42",
            new Dictionary<string, AgentModelConfiguration>(), null);
        string json = (string)typeof(RepositorySessionService)
            .GetMethod("SerializeWorkflowConfig", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [request])!;
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(expected, executionLimit);
        Assert.Equal(executionLimit, document.RootElement.GetProperty("limits").GetProperty("maxTotalMessages").GetInt32());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void NonpositiveMessageLimitIsRejected(string configured)
    {
        RepositorySessionService service = new(null!, null!, null!, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Workflow:MaxTotalMessages"] = configured }).Build());
        var exception = Assert.Throws<TargetInvocationException>(() => typeof(RepositorySessionService)
            .GetProperty("MaxTotalMessages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));
        Assert.IsType<ConfigurationException>(exception.InnerException);
    }

    [Fact]
    public void DefaultMessageLimitAllowsToolHeavyConversationWithoutApprovingRelease()
    {
        int limit = (int)typeof(RepositorySessionService)
            .GetProperty("MaxTotalMessages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Create(null))!;
        List<ChatMessage> conversation = [new(ChatRole.User, "Implement the change.")];
        for (int index = 0; index < 90; index++)
        {
            string callId = $"read-{index}";
            conversation.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(callId, "read_repository_file",
                    new Dictionary<string, object?> { ["path"] = "Service.cs" })]) { AuthorName = AgentNames.Developer });
            conversation.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, "source")]));
        }
        WorkflowSettings settings = new("https://example.test", "model", "key", MaxTotalMessages: limit);

        Assert.Null(HandoffTopology.GetTerminationStatus(conversation, settings));
        Assert.Equal("message_limit_reached",
            HandoffTopology.GetTerminationStatus(conversation, settings with { MaxTotalMessages = 100 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JiraValidationSelectionIsSaved(bool enabled)
    {
        CreateSessionRequest request = new("https://example.test/repo.git", "main", "TASK-42", new Dictionary<string, AgentModelConfiguration>(), null,
            JiraValidationEnabled: enabled, ReleaseManagerValidationEnabled: enabled);
        string json = (string)typeof(RepositorySessionService).GetMethod("SerializeWorkflowConfig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Create(null), [request])!;
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(enabled, document.RootElement.GetProperty("JiraValidationEnabled").GetBoolean());
        Assert.Equal(enabled, document.RootElement.GetProperty("ReleaseManagerValidationEnabled").GetBoolean());
    }

    private static RepositorySessionService Create(string? configured) => new(null!, null!, null!, null!, null!, null!, null!, null!,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Workflow:MaxAgentTurns"] = configured }).Build());

    [Theory]
    [InlineData(null, 120)]
    [InlineData(180, 180)]
    [InlineData(15, 15)]
    public void AgentTurnSelectionIsAppliedAndSaved(int? selected, int expected)
    {
        AgentModelConfiguration developer = new(new ModelProviderDefinition("model", "Test", "https://example.test",
            true, false, 8192, 2048, false, null, null), "key")
        { MaxAgentTurns = selected };
        CreateSessionRequest request = new("https://example.test/repo.git", "main", "TASK-42",
            new Dictionary<string, AgentModelConfiguration> { [AgentNames.Developer] = developer }, null);
        string json = (string)typeof(RepositorySessionService).GetMethod("SerializeWorkflowConfig", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Create(null), [request])!;
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(expected, document.RootElement.GetProperty("agents").GetProperty("developer").GetProperty("maxAgentTurns").GetInt32());
        Assert.Equal(expected, document.RootElement.GetProperty("limits").GetProperty("maxAgentTurns").GetInt32());
        Assert.Equal(expected, developer.ToSettings(100, AgentModelConfiguration.DefaultDeveloperMaxAgentTurns).MaxAgentTurns);
        Assert.Equal(200, request.MaxAiCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveAgentTurnOverrideIsRejected(int turns)
    {
        AgentModelConfiguration profile = new(new ModelProviderDefinition("model", "Test", "https://example.test",
            true, false, 8192, 2048, false, null, null), "key")
        { MaxAgentTurns = turns };
        Assert.Throws<ConfigurationException>(() => profile.ToSettings(100, 60));
    }
}
