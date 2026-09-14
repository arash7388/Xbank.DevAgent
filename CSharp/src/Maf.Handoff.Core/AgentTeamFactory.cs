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
        IReferenceDocumentSearch? referenceDocuments = null,
        ICodeGraphTools? codeGraphTools = null,
        CodeGraphAudit? codeGraphAudit = null)
    {
        Dictionary<string, IChatClient> clients = AgentNames.All.ToDictionary(name => name, _ => chatClient);
        Dictionary<string, WorkflowSettings> settingsByAgent = AgentNames.All.ToDictionary(name => name, _ => settings);
        return Create(clients, settingsByAgent, workspace, referenceDocuments, codeGraphTools, codeGraphAudit);
    }

    public static AgentTeam Create(
        IReadOnlyDictionary<string, IChatClient> clients,
        IReadOnlyDictionary<string, WorkflowSettings> settingsByAgent,
        RepositoryWorkspace? workspace = null,
        IReferenceDocumentSearch? referenceDocuments = null,
        ICodeGraphTools? codeGraphTools = null,
        CodeGraphAudit? codeGraphAudit = null)
    {
        bool codeGraphAvailable = workspace?.ContainsCSharpSource() is true && codeGraphTools is not null;
        if (codeGraphAvailable)
        {
            try
            {
                codeGraphTools!.EnsureAvailable(workspace!.RepositoryUrl);
            }
            catch (Exception)
            {
                // CodeGraph is an optional optimization. Repository tools remain the source-of-truth fallback.
                codeGraphAvailable = false;
            }
        }

        codeGraphAudit ??= new CodeGraphAudit();

        Dictionary<string, IChatClient> effectiveClients = clients.ToDictionary(
            pair => pair.Key,
            pair => (IChatClient)new ExclusiveHandoffChatClient(pair.Value),
            StringComparer.Ordinal);
        if (workspace is not null)
        {
            effectiveClients[AgentNames.Developer] = new DeveloperHandoffGuardChatClient(
                Get(effectiveClients, AgentNames.Developer),
                () => !string.IsNullOrWhiteSpace(workspace.GetDiff()));
        }

        return new(
            CreateAgent(Get(effectiveClients, AgentNames.Intake), Get(settingsByAgent, AgentNames.Intake), AgentNames.Intake, "Intake, constraints, and routing.", AgentPrompts.Intake, CreateTools(AgentNames.Intake, false), codeGraphAvailable),
            CreateOptionalAgent(effectiveClients, settingsByAgent, AgentNames.Jira, "Read-only Jira issue and project context retrieval.", AgentPrompts.Jira, tools: null, codeGraphAvailable),
            CreateOptionalAgent(effectiveClients, settingsByAgent, AgentNames.Planner, "Architecture and acceptance criteria.", AgentPrompts.Planner, CreateTools(AgentNames.Planner, false), codeGraphAvailable),
            CreateAgent(Get(effectiveClients, AgentNames.Developer), Get(settingsByAgent, AgentNames.Developer), AgentNames.Developer, "Implementation, tests, and remediation.", AgentPrompts.Developer, CreateTools(AgentNames.Developer, true), codeGraphAvailable),
            CreateOptionalAgent(effectiveClients, settingsByAgent, AgentNames.Reviewer, "Independent correctness and quality review.", AgentPrompts.Reviewer, CreateTools(AgentNames.Reviewer, false), codeGraphAvailable),
            CreateOptionalAgent(effectiveClients, settingsByAgent, AgentNames.Security, "Threat modeling and security review.", AgentPrompts.Security, CreateTools(AgentNames.Security, false), codeGraphAvailable),
            CreateAgent(Get(effectiveClients, AgentNames.ReleaseManager), Get(settingsByAgent, AgentNames.ReleaseManager), AgentNames.ReleaseManager, "Final evidence gate.", AgentPrompts.ReleaseManager, CreateTools(AgentNames.ReleaseManager, false), codeGraphAvailable));

        IList<AITool>? CreateTools(string agentName, bool developer)
        {
            List<AITool> tools;
            if (workspace is null)
            {
                tools = referenceDocuments is null
                    ? []
                    : [ReferenceDocumentToolFactory.Create(referenceDocuments)];
            }
            else
            {
                IList<AITool> repositoryTools = developer
                    ? RepositoryToolFactory.CreateDeveloperTools(workspace, referenceDocuments)
                    : RepositoryToolFactory.CreateReadTools(workspace, referenceDocuments);
                tools = [.. repositoryTools];
            }

            if (codeGraphAvailable && CodeGraphPolicy.IsUsefulFor(agentName))
            {
                tools.AddRange(CodeGraphToolFactory.Create(codeGraphTools!, codeGraphAudit!, agentName));
            }

            return tools.Count == 0 ? null : tools;
        }
    }

    private static ChatClientAgent? CreateOptionalAgent(
        IReadOnlyDictionary<string, IChatClient> clients,
        IReadOnlyDictionary<string, WorkflowSettings> settingsByAgent,
        string name,
        string description,
        string instructions,
        IList<AITool>? tools,
        bool codeGraphAvailable)
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

        return CreateAgent(client!, settings!, name, description, instructions, tools, codeGraphAvailable);
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
        IList<AITool>? tools,
        bool codeGraphAvailable) => new(client, new ChatClientAgentOptions
        {
            Id = $"software-delivery-{name}",
            Name = name,
            Description = description,
            ChatOptions = CreateChatOptions(
                settings,
                AgentInstructionComposer.Compose(name, instructions, settings.Prompt, codeGraphAvailable),
                tools),
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
            // Request usage on the final DeepSeek streaming chunk so the run collector can
            // aggregate provider-reported token counts.
            options.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["include_usage"] = true,
            };
            options.Reasoning = new ReasoningOptions
            {
                Effort = settings.Provider?.ReasoningEffort switch
                {
                    "low" => ReasoningEffort.Low,
                    "medium" => ReasoningEffort.Medium,
                    "max" => ReasoningEffort.ExtraHigh,
                    _ => ReasoningEffort.High,
                },
                // Keep reasoning available to the model without returning its trace in the
                // response, where it would increase output and subsequent context usage.
                Output = ReasoningOutput.None,
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
