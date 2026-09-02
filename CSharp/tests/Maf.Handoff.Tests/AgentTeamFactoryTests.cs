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

        Assert.Contains("developer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowsOptionalStagesToBeOmitted()
    {
        WorkflowSettings settings = new("https://example.test", "test-model", "test-key");
        RecordingChatClient client = new();
        Dictionary<string, IChatClient> clients = AgentNames.Required.ToDictionary(name => name, _ => (IChatClient)client);
        Dictionary<string, WorkflowSettings> settingsByAgent = AgentNames.Required.ToDictionary(name => name, _ => settings);

        AgentTeam team = AgentTeamFactory.Create(clients, settingsByAgent);

        Assert.Null(team.Planner);
        Assert.Null(team.Reviewer);
        Assert.Null(team.Security);
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
        Assert.Null(bundle.Team.Planner);
        Assert.Null(bundle.Team.Reviewer);
        Assert.Null(bundle.Team.Security);
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
    }

    [Fact]
    public void UsesCustomAgentPromptAsInstructions()
    {
        WorkflowSettings settings = new(
            "https://api.openai.com/v1",
            "gpt-5.6-terra",
            "test-key",
            Prompt: "Role-specific custom prompt");

        ChatOptions options = AgentTeamFactory.CreateChatOptions(settings, settings.Prompt!, tools: null);

        Assert.Equal("Role-specific custom prompt", options.Instructions);
    }

    [Fact]
    public void PublishesDefaultPromptForEveryAgent()
    {
        Assert.Equal(AgentNames.All, AgentPrompts.Defaults.Keys);
        Assert.All(AgentPrompts.Defaults.Values, prompt => Assert.False(string.IsNullOrWhiteSpace(prompt)));
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
