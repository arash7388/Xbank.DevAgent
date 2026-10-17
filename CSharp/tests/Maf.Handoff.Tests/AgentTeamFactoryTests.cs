using Maf.Handoff.Core;
using Microsoft.Extensions.AI;
using System.Reflection;

namespace Maf.Handoff.Tests;

public sealed class AgentTeamFactoryTests
{
    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    public async Task EveryRequiredAgentCanRequestDecisionsWithoutJira(int level)
    {
        RecordingChatClient client = new();
        WorkflowSettings settings = new("https://example.test", "model", "key") { AutonomyLevel = level };
        DecisionInput input = new();
        AgentTeam team = AgentTeamFactory.Create(client, settings, userInput: input);
        foreach (var agent in team.All)
        {
            await agent.RunAsync("Inspect the assigned task");
            var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(client.Options!.Tools!, tool => tool.Name == "ask_user_decision"));
            var response = await tool.InvokeAsync(new AIFunctionArguments { ["questions"] = "Which approach?" });
            Assert.Equal(level == 100 ? AutonomyPolicy.DelegatedDecision : "Use the existing approach", response?.ToString());
        }
        Assert.Equal(level == 100 ? 0 : AgentNames.Required.Count, input.Calls);
    }

    private sealed class DecisionInput : IWorkflowUserInput
    {
        public int Calls { get; private set; }
        public Task<string> AskAsync(string questions, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult("Use the existing approach");
        }
    }

    [Fact]
    public void RejectsConfigurationWithoutMandatoryReviewer()
    {
        WorkflowSettings settings = new("https://example.test", "test-model", "test-key");
        string[] names = [AgentNames.Planner, AgentNames.Developer, AgentNames.ReleaseManager];
        Dictionary<string, IChatClient> clients = names.ToDictionary(name => name, _ => (IChatClient)new RecordingChatClient());

        ConfigurationException exception = Assert.Throws<ConfigurationException>(() =>
            HandoffWorkflowFactory.Create(clients, names.ToDictionary(name => name, _ => settings)));

        Assert.Contains("reviewer", exception.Message, StringComparison.Ordinal);
    }

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
        Assert.NotNull(team.Reviewer);
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
        Assert.NotNull(bundle.Team.Reviewer);
        Assert.Null(bundle.Team.Security);
    }

    [Fact]
    public void AddsOptionalJiraAgentAsTheStartingStage()
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
    public async Task JiraCanReadRepositoryAndReferenceDocumentsWithoutWriteAccess()
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-jira-intake", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (System.Diagnostics.Process git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = root, ArgumentList = { "init", "--quiet" },
                UseShellExecute = false, CreateNoWindow = true,
            })!)
            {
                git.WaitForExit();
                Assert.Equal(0, git.ExitCode);
            }
            File.WriteAllText(Path.Combine(root, "settings.json"), "{ \"client\": \"ExistingConsumer\" }");
            RepositoryWorkspace workspace = (RepositoryWorkspace)Activator.CreateInstance(
                typeof(RepositoryWorkspace), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [root, "https://github.com/example/repository", "main", null], culture: null)!;
            WorkflowSettings settings = new("https://example.test", "test-model", "test-key");
            RecordingChatClient client = new();
            string[] names = [.. AgentNames.Required, AgentNames.Jira];
            AgentTeam team = AgentTeamFactory.Create(
                names.ToDictionary(name => name, _ => (IChatClient)client),
                names.ToDictionary(name => name, _ => settings), workspace, new IntakeDocuments());

            await team.Jira!.RunAsync("Assess integration requirements.");
            IList<AITool> tools = client.Options!.Tools!;
            AIFunction read = Assert.IsAssignableFrom<AIFunction>(Assert.Single(tools, tool => tool.Name == AgentToolCatalog.ReadRepositoryFile.Name));
            AIFunction search = Assert.IsAssignableFrom<AIFunction>(Assert.Single(tools, tool => tool.Name == AgentToolCatalog.SearchDocuments.Name));
            Assert.Contains("ExistingConsumer", (await read.InvokeAsync(new AIFunctionArguments { ["path"] = "settings.json" }))?.ToString());
            Assert.Contains("API compatibility", (await search.InvokeAsync(new AIFunctionArguments { ["query"] = "client integration", ["maxResults"] = 3 }))?.ToString());
            Assert.Contains(tools, tool => tool.Name == AgentToolCatalog.SearchRepository.Name);
            Assert.DoesNotContain(tools, tool => tool.Name is "write_repository_file" or "replace_repository_text");
            Assert.DoesNotContain(tools, tool => tool.Name.StartsWith("get_jira_", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
        Assert.Equal(25, settings.MaxLlmRequests);
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
        Assert.Contains(
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
        Assert.Contains(AgentToolCatalog.GetJiraIssueDetails, AgentToolCatalog.ByAgent[AgentNames.Jira]);
        Assert.Contains(AgentToolCatalog.GetJiraIssueComments, AgentToolCatalog.ByAgent[AgentNames.Jira]);
        Assert.Contains(AgentToolCatalog.ReadRepositoryFile, AgentToolCatalog.ByAgent[AgentNames.Jira]);
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
            AgentToolCatalog.ByAgent,
            entry => Assert.Contains(
                entry.Value,
                tool => tool.Name == AgentToolCatalog.SearchDocuments.Name
                    && tool.Availability == AgentToolCatalog.WithReferenceDocuments));
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public ChatOptions? Options { get; private set; }
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Options = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Test response")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Options = options;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Test response");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class IntakeDocuments : IReferenceDocumentSearch
    {
        public Task<string> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            Task.FromResult("architecture.md chunk 1: API compatibility requires support for ExistingConsumer.");
    }
}
