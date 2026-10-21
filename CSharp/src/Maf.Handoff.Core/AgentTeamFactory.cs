using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Maf.Handoff.Core;

public static class AgentTeamFactory
{
    private const int PlannerToolRoundLimit = 3;

    public static AgentTeam Create(
        IChatClient chatClient,
        WorkflowSettings settings,
        RepositoryWorkspace? workspace = null,
        IReferenceDocumentSearch? referenceDocuments = null,
        ICodeGraphTools? codeGraphTools = null,
        CodeGraphAudit? codeGraphAudit = null,
        IToolCallStore? toolCallStore = null,
        string? workflowId = null,
        IJiraIssueSearch? jira = null,
        IWorkflowUserInput? userInput = null,
        JiraIntakeCheckpoint? jiraIntakeCheckpoint = null)
    {
        Dictionary<string, IChatClient> clients = AgentNames.Required.ToDictionary(name => name, _ => chatClient);
        Dictionary<string, WorkflowSettings> settingsByAgent = AgentNames.Required.ToDictionary(name => name, _ => settings);
        return Create(clients, settingsByAgent, workspace, referenceDocuments, codeGraphTools, codeGraphAudit, toolCallStore, workflowId, jira, userInput, jiraIntakeCheckpoint);
    }

    public static AgentTeam Create(
        IReadOnlyDictionary<string, IChatClient> clients,
        IReadOnlyDictionary<string, WorkflowSettings> settingsByAgent,
        RepositoryWorkspace? workspace = null,
        IReferenceDocumentSearch? referenceDocuments = null,
        ICodeGraphTools? codeGraphTools = null,
        CodeGraphAudit? codeGraphAudit = null,
        IToolCallStore? toolCallStore = null,
        string? workflowId = null,
        IJiraIssueSearch? jira = null,
        IWorkflowUserInput? userInput = null,
        JiraIntakeCheckpoint? jiraIntakeCheckpoint = null)
    {
        WorkflowSettings workflowSettings = Get(settingsByAgent, AgentNames.Developer);
        userInput ??= jira as IWorkflowUserInput;
        if (userInput is not null && userInput is not AutonomyUserInput)
            userInput = new AutonomyUserInput(workflowSettings.AutonomyLevel, userInput);
        workflowSettings.Validate();
        // Jira access, including Developer's comment reporting, requires the Jira agent.
        if (!clients.ContainsKey(AgentNames.Jira) || !settingsByAgent.ContainsKey(AgentNames.Jira))
            jira = null;
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
        ToolResultCache readCache = new();

        Dictionary<string, IChatClient> effectiveClients = clients.ToDictionary(
            pair => pair.Key,
            pair => CreateEffectiveClient(pair.Key, pair.Value),
            StringComparer.Ordinal);
        if (workspace is not null)
        {
            effectiveClients[AgentNames.Developer] = new DeveloperHandoffGuardChatClient(
                Get(effectiveClients, AgentNames.Developer),
                () => !string.IsNullOrWhiteSpace(workspace.GetDiff()));
        }

        effectiveClients[AgentNames.Reviewer] = new ReviewerApprovalChatClient(
            Get(effectiveClients, AgentNames.Reviewer));

        ReviewerRemediationBudget remediationBudget = new(
            workflowSettings.MaxReviewerRemediationCycles);
        WorkflowExecutionBudget executionBudget = new(workflowSettings);
        WorkflowLoopBudget loopBudget = new(workflowSettings.MaxRepeatedWorkflowStates);
        effectiveClients = effectiveClients.ToDictionary(
            pair => pair.Key,
            pair => (IChatClient)new WorkflowLoopGuardChatClient(new ReviewerRemediationGuardChatClient(
                new WorkflowExecutionGuardChatClient(pair.Value, pair.Key, executionBudget),
                pair.Key, remediationBudget), pair.Key, loopBudget, () => workspace?.GetProgressFingerprint() ?? ""),
            StringComparer.Ordinal);

        // Clarification retries must pass through the same execution and request budgets.
        if (effectiveClients.TryGetValue(AgentNames.Jira, out IChatClient? intake) &&
            userInput is { IsAvailable: true } input)
            effectiveClients[AgentNames.Jira] = new JiraClarificationChatClient(intake, input);

        return new(
            CreateOptionalAgent(effectiveClients, settingsByAgent, AgentNames.Jira, "Task readiness, requirements validation, and integration reconnaissance.", AgentPrompts.Jira,
                CreateTools(AgentNames.Jira, false), codeGraphAvailable),
            CreateAgent(Get(effectiveClients, AgentNames.Planner), Get(settingsByAgent, AgentNames.Planner), AgentNames.Planner, "Architecture and acceptance criteria.", AgentPrompts.Planner, CreateTools(AgentNames.Planner, false), codeGraphAvailable),
            CreateAgent(Get(effectiveClients, AgentNames.Developer), Get(settingsByAgent, AgentNames.Developer), AgentNames.Developer, "Implementation, tests, and remediation.", AgentPrompts.Developer, CreateTools(AgentNames.Developer, true), codeGraphAvailable),
            CreateAgent(Get(effectiveClients, AgentNames.Reviewer), Get(settingsByAgent, AgentNames.Reviewer), AgentNames.Reviewer, "Independent correctness and quality review.", AgentPrompts.Reviewer, CreateTools(AgentNames.Reviewer, false), codeGraphAvailable),
            CreateOptionalAgent(effectiveClients, settingsByAgent, AgentNames.Security, "Threat modeling and security review.", AgentPrompts.Security, CreateTools(AgentNames.Security, false), codeGraphAvailable),
            CreateAgent(Get(effectiveClients, AgentNames.ReleaseManager), Get(settingsByAgent, AgentNames.ReleaseManager), AgentNames.ReleaseManager, "Final evidence gate.", AgentPrompts.ReleaseManager, CreateTools(AgentNames.ReleaseManager, false), codeGraphAvailable));

        IChatClient CreateEffectiveClient(string agentName, IChatClient client)
        {
            if (agentName == AgentNames.Jira && jiraIntakeCheckpoint is not null)
                client = jiraIntakeCheckpoint.Wrap(client);
            if (agentName == AgentNames.Jira && jira is IJiraProblemReporter reporter)
                client = new JiraProblemReportingChatClient(client, reporter);
            if (agentName == AgentNames.Developer && jira is IJiraScopeChangeReporter changes)
                client = new JiraScopeChangeReportingChatClient(client, changes);
            IChatClient effective = codeGraphAvailable && CodeGraphPolicy.IsUsefulFor(agentName)
                ? new CodeGraphPrefetchChatClient(client)
                : client;
            if (agentName == AgentNames.Planner)
            {
                effective = new ToolRoundBudgetChatClient(effective, PlannerToolRoundLimit);
            }

            return new ExclusiveHandoffChatClient(effective);
        }

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
                    : RepositoryToolFactory.CreateReadTools(
                        workspace,
                        referenceDocuments,
                        compactFileReads: agentName is AgentNames.Planner or AgentNames.Jira);
                tools = [.. repositoryTools];
            }

            if (agentName == AgentNames.Jira && jira is not null)
            {
                tools.AddRange(JiraToolFactory.Create(jira, userInput));
            }
            if (userInput is { IsAvailable: true } decisions)
                tools.Add(AIFunctionFactory.Create(
                    (string questions, CancellationToken cancellationToken) => decisions.AskAsync(questions, cancellationToken),
                    name: "ask_user_decision", description: "Ask the user for a decision required by the selected autonomy policy. At 100% returns delegated decision guidance immediately."));
            if (agentName == AgentNames.Developer && jira is IJiraScopeChangeReporter changes)
                tools.Add(AIFunctionFactory.Create(
                    (string changesInPersian, CancellationToken cancellationToken) => changes.ReportScopeChangeAsync(changesInPersian, cancellationToken),
                    name: AgentToolCatalog.ReportJiraScopeChange.Name, description: AgentToolCatalog.ReportJiraScopeChange.Description));

            if (codeGraphAvailable && CodeGraphPolicy.IsUsefulFor(agentName))
            {
                tools.AddRange(CodeGraphToolFactory.Create(codeGraphTools!, codeGraphAudit!, agentName));
            }

            return tools.Count == 0 ? null : AuditTools(tools, agentName);
        }

        IList<AITool> AuditTools(IList<AITool> tools, string agentName) =>
            [.. tools.Select(tool => tool is AIFunction function ? WrapTool(function, agentName) : tool)];

        AITool WrapTool(AIFunction function, string agentName)
        {
            string name = function.Name;
            // Auditing stays inside caching: tool_calls measures actual executions only.
            AIFunction audited = toolCallStore is not null && !string.IsNullOrWhiteSpace(workflowId)
                ? new ToolCallAuditingAIFunction(function, toolCallStore, workflowId, agentName) : function;
            Func<AIFunctionArguments, string?>? version = null;
            TimeSpan lifetime = TimeSpan.FromHours(1);
            string scope = name;
            if (workspace is not null && name is "read_repository_file" or "list_repository_files" or
                "search_repository" or "get_repository_status" or "get_repository_diff")
            {
                if (name == "read_repository_file")
                {
                    scope += agentName is AgentNames.Planner or AgentNames.Jira ? ":compact" : ":full";
                    version = args => args.TryGetValue("path", out object? path) && path is not null
                        ? workspace.GetReadCacheVersion(path.ToString()) : null;
                }
                else version = _ => workspace.GetReadCacheVersion();
            }
            else if (codeGraphTools is not null && name is "find_symbol" or "get_neighbors" or "get_context" or "get_context_by_id")
                version = _ => codeGraphTools.GetCacheVersion();
            else if (name is "get_jira_issue_details" or "get_jira_issue_comments" or "list_jira_attachments" or
                "read_jira_attachment" or "search_jira_issues" or "get_jira_issue_title")
            {
                lifetime = TimeSpan.FromSeconds(30);
                version = _ => readCache.JiraVersion;
            }
            if (name is "report_jira_problem" or "report_jira_scope_change" or "ask_jira_clarification" or "ask_user_decision")
                return new JiraCacheInvalidatingAIFunction(audited, readCache);
            return version is null ? audited : new CachedReadAIFunction(audited, readCache, version, scope, lifetime,
                async (key, args, result, token) =>
                {
                    if (CodeGraphToolFactory.IsRelationshipTool(name))
                    {
                        object? subject = args.GetValueOrDefault(name == "get_context" ? "query" : "id");
                        CodeGraphToolFactory.RecordCached(codeGraphAudit, agentName, name, subject?.ToString() ?? "", result?.ToString() ?? "");
                    }
                    if (toolCallStore is null || string.IsNullOrWhiteSpace(workflowId)) return;
                    try
                    {
                        await toolCallStore.RecordToolCacheHitAsync(workflowId, agentName, name, key,
                            System.Text.Json.JsonSerializer.Serialize(args), token).ConfigureAwait(false);
                    }
                    catch { /* Diagnostics must not change a cached tool result. */ }
                });
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
            Instructions = instructions + AutonomyPolicy.Instructions(settings.AutonomyLevel) + (settings.SkipBuildVerification
                ? "\n\nRUN BUILD POLICY: The user selected Skip build verification. Do not run or request a build for this run. " +
                  "Continue independent code/security review and the release decision without requiring a successful build. " +
                  "Do not block solely because build verification is unavailable. Report it as skipped, never passed."
                : "\n\nRUN BUILD POLICY: The workflow runs the solution build after the agents finish applying and reviewing changes. " +
                  "Report the build result to the user only; do not request build repair loops or claim compilation succeeded before the build runs."),
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
            if (settings.Provider?.Thinking is true)
            {
                options.Reasoning = new ReasoningOptions
                {
                    Effort = settings.Provider.ReasoningEffort switch
                    {
                        "low" => ReasoningEffort.Low,
                        "medium" => ReasoningEffort.Medium,
                        "max" => ReasoningEffort.ExtraHigh,
                        _ => ReasoningEffort.High,
                    },
                    // DeepSeek requires the reasoning_content associated with a tool call to be
                    // replayed on the next request. It remains internal workflow state and is not
                    // emitted by HandoffWorkflowRunner as user-visible message text.
                    Output = ReasoningOutput.Full,
                };
            }

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
