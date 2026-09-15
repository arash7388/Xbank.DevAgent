using Maf.Handoff.Core;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class AgentTeamFactoryTests
{
    [Fact]
    public void RequiresAConfigurationForEveryAgent()
    {
        Dictionary<string, IChatClient> clients = [];
        Dictionary<string, WorkflowSettings> settings = [];

        ConfigurationException exception = Assert.Throws<ConfigurationException>(() =>
            AgentTeamFactory.Create(clients, settings));

        Assert.Contains("required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowsOptionalStagesToBeOmitted()
    {
        WorkflowSettings settings = new("https://example.test", "test-model", "test-key");
        RecordingChatClient client = new();
        Dictionary<string, IChatClient> clients = AgentNames.Required.ToDictionary(name => name, _ => (IChatClient)client);
        Dictionary<string, WorkflowSettings> settingsByAgent = AgentNames.Required.ToDictionary(name => name, _ => settings);

        AgentTeam team = AgentTeamFactory.Create(clients, settingsByAgent);

        Assert.NotNull(team.Planner);
        Assert.Null(team.Jira);
        Assert.Null(team.Reviewer);
        Assert.Null(team.Security);
        Assert.Equal(AgentNames.Planner, team.StartAgent.Name);
        Assert.Equal(AgentNames.Required, team.All.Select(agent => agent.Name));
    }

    [Fact]
    public void SingleSettingsOverloadUsesOnlyRequiredStages()
    {
        WorkflowSettings settings = new("https://example.test", "test-model", "test-key");

        AgentTeam team = AgentTeamFactory.Create(new RecordingChatClient(), settings);

        Assert.Equal(AgentNames.Planner, team.StartAgent.Name);
        Assert.Equal(AgentNames.Required, team.All.Select(agent => agent.Name));
    }

    [Fact]
    public void BuildsAWorkflowThatSkipsAllOptionalStages()
    {
        WorkflowSettings settings = new("https://example.test", "test-model", "test-key");
        RecordingChatClient client = new();
        Dictionary<string, IChatClient> clients = AgentNames.Required.ToDictionary(name => name, _ => (IChatClient)client);
        Dictionary<string, WorkflowSettings> settingsByAgent = AgentNames.Required.ToDictionary(name => name, _ => settings);

        WorkflowBundle bundle = HandoffWorkflowFactory.Create(clients, settingsByAgent);

        Assert.NotNull(bundle.Workflow);
        Assert.NotNull(bundle.Team.Planner);
        Assert.Null(bundle.Team.Jira);
        Assert.Null(bundle.Team.Reviewer);
        Assert.Null(bundle.Team.Security);
    }

    [Fact]
    public void AddsOptionalJiraAgentWithoutGrantingRepositoryTools()
    {
        WorkflowSettings settings = new("https://example.test", "test-model", "test-key");
        RecordingChatClient client = new();
        Dictionary<string, IChatClient> clients = AgentNames.Required
            .Append(AgentNames.Jira)
            .ToDictionary(name => name, _ => (IChatClient)client);
        Dictionary<string, WorkflowSettings> settingsByAgent = AgentNames.Required
            .Append(AgentNames.Jira)
            .ToDictionary(name => name, _ => settings);

        AgentTeam team = AgentTeamFactory.Create(clients, settingsByAgent);

        Assert.NotNull(team.Jira);
        Assert.Equal(AgentNames.Jira, team.Jira.Name);
        Assert.Equal(AgentNames.Jira, team.StartAgent.Name);
    }

    [Fact]
    public void StartsWithPlannerWhenJiraIsOmitted()
    {
        WorkflowSettings settings = new("https://example.test", "test-model", "test-key");
        RecordingChatClient client = new();
        Dictionary<string, IChatClient> clients = AgentNames.Required
            .ToDictionary(name => name, _ => (IChatClient)client);
        Dictionary<string, WorkflowSettings> settingsByAgent = AgentNames.Required
            .ToDictionary(name => name, _ => settings);

        AgentTeam team = AgentTeamFactory.Create(clients, settingsByAgent);

        Assert.Equal(AgentNames.Planner, team.StartAgent.Name);
    }

    [Fact]
    public void ConvertsAnAgentProfileToIndependentSettings()
    {
        ModelProviderDefinition provider = new(
            "gpt-5.6-sol",
            "OpenAI",
            "https://api.openai.com/v1",
            ToolCalling: true,
            Vision: true,
            MaxInputTokens: 1_050_000,
            MaxOutputTokens: 128_000,
            Thinking: true,
            ReasoningEffort: "max",
            SupportsReasoningEffort: ["low", "high", "max"]);

        WorkflowSettings settings = new AgentModelConfiguration(provider, " agent-key ", " custom prompt ")
            .ToSettings(maxTotalMessages: 80, maxAgentTurns: 20);

        Assert.Equal("gpt-5.6-sol", settings.Model);
        Assert.Equal("agent-key", settings.ApiKey);
        Assert.Equal("max", settings.Provider!.ReasoningEffort);
        Assert.Equal("custom prompt", settings.Prompt);
        Assert.Equal(80, settings.MaxTotalMessages);
        Assert.Equal(20, settings.MaxAgentTurns);
        Assert.Equal(20, settings.MaxLlmRequests);
    }

    [Theory]
    [InlineData("low", ReasoningEffort.Low)]
    [InlineData("medium", ReasoningEffort.Medium)]
    [InlineData("high", ReasoningEffort.High)]
    [InlineData("max", ReasoningEffort.ExtraHigh)]
    public void UsesSelectedDeepSeekReasoningEffort(string selectedEffort, ReasoningEffort expectedEffort)
    {
        ModelProviderDefinition provider = new(
            "deepseek-v4-pro",
            "DeepSeek",
            "https://api.deepseek.com",
            ToolCalling: true,
            Vision: true,
            MaxInputTokens: 128_000,
            MaxOutputTokens: 160_000,
            Thinking: true,
            ReasoningEffort: selectedEffort,
            SupportsReasoningEffort: ["low", "medium", "high", "max"]);
        WorkflowSettings settings = new(provider.Url, provider.Id, "test-key", Provider: provider);

        ChatOptions options = AgentTeamFactory.CreateChatOptions(settings, "test", tools: null);

        Assert.Equal(expectedEffort, options.Reasoning!.Effort);
        Assert.Equal(ReasoningOutput.Full, options.Reasoning.Output);
        Assert.True(Assert.IsType<bool>(options.AdditionalProperties!["include_usage"]));
    }

    [Fact]
    public void DoesNotEnableDeepSeekReasoningWhenThinkingIsDisabled()
    {
        ModelProviderDefinition provider = new(
            "deepseek-chat",
            "DeepSeek",
            "https://api.deepseek.com",
            ToolCalling: true,
            Vision: false,
            MaxInputTokens: 128_000,
            MaxOutputTokens: 8_000,
            Thinking: false,
            ReasoningEffort: null,
            SupportsReasoningEffort: null);
        WorkflowSettings settings = new(provider.Url, provider.Id, "test-key", Provider: provider);

        ChatOptions options = AgentTeamFactory.CreateChatOptions(settings, "test", tools: null);

        Assert.Null(options.Reasoning);
        Assert.True(Assert.IsType<bool>(options.AdditionalProperties!["include_usage"]));
    }

    [Fact]
    public void ComposesCallerGuidanceWithComplementaryCodeGraphInstructions()
    {
        WorkflowSettings settings = new(
            "https://api.openai.com/v1",
            "gpt-5.6-terra",
            "test-key",
            Prompt: "Do not use CodeGraph.");

        string instructions = AgentInstructionComposer.Compose(
            AgentNames.Developer,
            AgentPrompts.Developer,
            settings.Prompt,
            codeGraphAvailable: true);
        ChatOptions options = AgentTeamFactory.CreateChatOptions(settings, instructions, tools: null);

        Assert.Contains("implementation specialist", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("Do not use CodeGraph.", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("CODEGRAPH GUIDANCE", options.Instructions, StringComparison.Ordinal);
        Assert.Contains("never a handoff gate", options.Instructions, StringComparison.Ordinal);
        Assert.EndsWith(
            "Configuration, documentation, and other non-C# work normally needs no graph call.",
            options.Instructions,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PublishesDefaultPromptForEveryAgent()
    {
        Assert.Equal(AgentNames.All, AgentPrompts.Defaults.Keys);
        Assert.All(AgentPrompts.Defaults.Values, prompt => Assert.False(string.IsNullOrWhiteSpace(prompt)));
    }

    [Fact]
    public void PublishesAvailableToolsForEveryAgent()
    {
        Assert.Equal(AgentNames.All, AgentToolCatalog.ByAgent.Keys);
        Assert.Empty(AgentToolCatalog.ByAgent[AgentNames.Jira]);
        Assert.Contains(
            AgentToolCatalog.WriteRepositoryFile,
            AgentToolCatalog.ByAgent[AgentNames.Developer]);
        Assert.All(
            new[] { AgentNames.Planner, AgentNames.Developer, AgentNames.Reviewer, AgentNames.Security },
            agent => Assert.Contains(
                AgentToolCatalog.GetContext,
                AgentToolCatalog.ByAgent[agent]));
        Assert.DoesNotContain(
            AgentToolCatalog.GetContext,
            AgentToolCatalog.ByAgent[AgentNames.ReleaseManager]);
        Assert.All(
            AgentToolCatalog.ByAgent.Where(entry => entry.Key != AgentNames.Developer),
            entry => Assert.DoesNotContain(AgentToolCatalog.WriteRepositoryFile, entry.Value));
        Assert.All(
            AgentToolCatalog.ByAgent.Where(entry => entry.Key != AgentNames.Jira),
            entry => Assert.Contains(
                entry.Value,
                tool => tool.Name == AgentToolCatalog.SearchDocuments.Name
                    && tool.Availability == AgentToolCatalog.WithReferenceDocuments));
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Test response")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Test response");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
