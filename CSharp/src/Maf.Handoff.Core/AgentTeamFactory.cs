using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Maf.Handoff.Core;

public static class AgentTeamFactory
{
    public static AgentTeam Create(
        IChatClient chatClient,
        WorkflowSettings settings,
        RepositoryWorkspace? workspace = null,
        IReferenceDocumentSearch? referenceDocuments = null)
    {
        Dictionary<string, IChatClient> clients = AgentNames.All.ToDictionary(name => name, _ => chatClient);
        Dictionary<string, WorkflowSettings> settingsByAgent = AgentNames.All.ToDictionary(name => name, _ => settings);
        return Create(clients, settingsByAgent, workspace, referenceDocuments);
    }

    public static AgentTeam Create(
        IReadOnlyDictionary<string, IChatClient> clients,
        IReadOnlyDictionary<string, WorkflowSettings> settingsByAgent,
        RepositoryWorkspace? workspace = null,
        IReferenceDocumentSearch? referenceDocuments = null)
    {
        IList<AITool>? readTools = workspace is null
            ? referenceDocuments is null ? null : [ReferenceDocumentToolFactory.Create(referenceDocuments)]
            : RepositoryToolFactory.CreateReadTools(workspace, referenceDocuments);
        IList<AITool>? developerTools = workspace is null
            ? referenceDocuments is null ? null : [ReferenceDocumentToolFactory.Create(referenceDocuments)]
            : RepositoryToolFactory.CreateDeveloperTools(workspace, referenceDocuments);
        IChatClient developerClient = workspace is null
            ? Get(clients, AgentNames.Developer)
            : new DeveloperHandoffGuardChatClient(
                Get(clients, AgentNames.Developer),
                () => !string.IsNullOrWhiteSpace(workspace.GetDiff()));

        return new(
            CreateAgent(Get(clients, AgentNames.Intake), Get(settingsByAgent, AgentNames.Intake), AgentNames.Intake, "Intake, constraints, and routing.", AgentPrompts.Intake, readTools),
            CreateOptionalAgent(clients, settingsByAgent, AgentNames.Jira, "Read-only Jira issue and project context retrieval.", AgentPrompts.Jira, tools: null),
            CreateOptionalAgent(clients, settingsByAgent, AgentNames.Planner, "Architecture and acceptance criteria.", AgentPrompts.Planner, readTools),
            CreateAgent(developerClient, Get(settingsByAgent, AgentNames.Developer), AgentNames.Developer, "Implementation, tests, and remediation.", AgentPrompts.Developer, developerTools),
            CreateOptionalAgent(clients, settingsByAgent, AgentNames.Reviewer, "Independent correctness and quality review.", AgentPrompts.Reviewer, readTools),
            CreateOptionalAgent(clients, settingsByAgent, AgentNames.Security, "Threat modeling and security review.", AgentPrompts.Security, readTools),
            CreateAgent(Get(clients, AgentNames.ReleaseManager), Get(settingsByAgent, AgentNames.ReleaseManager), AgentNames.ReleaseManager, "Final evidence gate.", AgentPrompts.ReleaseManager, readTools));
    }

    private static ChatClientAgent? CreateOptionalAgent(
        IReadOnlyDictionary<string, IChatClient> clients,
        IReadOnlyDictionary<string, WorkflowSettings> settingsByAgent,
        string name,
        string description,
        string instructions,
        IList<AITool>? tools)
    {
        bool hasSettings = settingsByAgent.TryGetValue(name, out WorkflowSettings? settings);
        bool hasClient = clients.TryGetValue(name, out IChatClient? client);
        if (!hasSettings && !hasClient)
        {
            return null;
        }

        if (!hasSettings || !hasClient)
        {
            throw new ConfigurationException($"Model configuration for optional agent '{name}' is incomplete.");
        }

        return CreateAgent(client!, settings!, name, description, instructions, tools);
    }

    private static T Get<T>(IReadOnlyDictionary<string, T> values, string agentName) =>
        values.TryGetValue(agentName, out T? value)
            ? value
            : throw new ConfigurationException($"Model configuration for agent '{agentName}' is required.");

    private static ChatClientAgent CreateAgent(
        IChatClient client,
        WorkflowSettings settings,
        string name,
        string description,
        string instructions,
        IList<AITool>? tools = null) => new(client, new ChatClientAgentOptions
        {
            Id = $"software-delivery-{name}",
            Name = name,
            Description = description,
            ChatOptions = CreateChatOptions(settings, settings.Prompt ?? instructions, tools),
        });

    internal static ChatOptions CreateChatOptions(
        WorkflowSettings settings,
        string instructions,
        IList<AITool>? tools)
    {
        ChatOptions options = new()
        {
            Instructions = instructions,
            Tools = tools,
        };

        string? providerHost = settings.Provider is null ? null : new Uri(settings.Provider.Url).Host;
        bool isAnthropic = providerHost?.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase) is true;
        bool isDeepSeek = providerHost?.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase) is true;
        if (isDeepSeek)
        {
            options.Reasoning = new ReasoningOptions
            {
                Effort = settings.Provider?.ReasoningEffort switch
                {
                    "low" => ReasoningEffort.Low,
                    "medium" => ReasoningEffort.Medium,
                    "max" => ReasoningEffort.ExtraHigh,
                    _ => ReasoningEffort.High,
                },
                Output = ReasoningOutput.Full,
            };
            return options;
        }

        string? effort = settings.Provider?.Thinking is true && !isAnthropic
            ? settings.Provider.ReasoningEffort
            : null;
        if (!string.IsNullOrWhiteSpace(effort))
        {
#pragma warning disable OPENAI001 // Chat-completions reasoning options are experimental in OpenAI 2.x.
            options.RawRepresentationFactory = _ => new ChatCompletionOptions
            {
                ReasoningEffortLevel = new ChatReasoningEffortLevel(effort),
            };
#pragma warning restore OPENAI001
        }

        return options;
    }
}
