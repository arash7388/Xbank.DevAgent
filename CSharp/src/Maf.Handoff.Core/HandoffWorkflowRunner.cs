using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

public sealed class HandoffWorkflowRunner
{
    private readonly WorkflowSettings? _settings;
    private readonly IReadOnlyDictionary<string, WorkflowSettings>? _settingsByAgent;
    private readonly RepositoryWorkspace? _workspace;
    private readonly IChatClient? _chatClient;
    private readonly IReferenceDocumentSearch? _referenceDocuments;
    private readonly ICodeGraphTools? _codeGraphTools;
    private readonly IJiraIssueSearch? _jira;
    private readonly ILlmCheckpointStore? _checkpointStore;

    public HandoffWorkflowRunner(
        WorkflowSettings settings,
        RepositoryWorkspace? workspace = null,
        IChatClient? chatClient = null,
        IReferenceDocumentSearch? referenceDocuments = null,
        ICodeGraphTools? codeGraphTools = null,
        ILlmCheckpointStore? checkpointStore = null,
        IJiraIssueSearch? jira = null)
    {
        _settings = settings;
        _workspace = workspace;
        _chatClient = chatClient;
        _referenceDocuments = referenceDocuments;
        _codeGraphTools = codeGraphTools;
        _jira = jira;
        _checkpointStore = checkpointStore;
    }

    public HandoffWorkflowRunner(
        IReadOnlyDictionary<string, WorkflowSettings> settingsByAgent,
        RepositoryWorkspace? workspace = null,
        IReferenceDocumentSearch? referenceDocuments = null,
        ICodeGraphTools? codeGraphTools = null,
        ILlmCheckpointStore? checkpointStore = null,
        IJiraIssueSearch? jira = null)
    {
        _settingsByAgent = settingsByAgent;
        _workspace = workspace;
        _referenceDocuments = referenceDocuments;
        _codeGraphTools = codeGraphTools;
        _jira = jira;
        _checkpointStore = checkpointStore;
    }

    public async IAsyncEnumerable<RunEvent> RunAsync(
        string task,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        string? workflowId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        workflowId ??= Guid.NewGuid().ToString("N");

        WorkflowBundle bundle;
        WorkflowSettings workflowSettings;
        IReadOnlyDictionary<string, WorkflowSettings> settingsByAgent;
        TokenUsageCollector usageCollector;
        LlmCheckpointMetrics checkpointMetrics = new();
        CodeGraphAudit codeGraphAudit = new();
        if (_settingsByAgent is not null)
        {
            Dictionary<string, IChatClient> clients = [];
            foreach ((string agentName, WorkflowSettings agentSettings) in _settingsByAgent)
            {
                clients[agentName] = ModelChatClientFactory.Create(agentSettings);
            }

            foreach (string requiredAgent in AgentNames.Required)
            {
                if (!clients.ContainsKey(requiredAgent))
                {
                    throw new ConfigurationException($"Model configuration for agent '{requiredAgent}' is required.");
                }
            }

            workflowSettings = _settingsByAgent[AgentNames.Developer];
            settingsByAgent = _settingsByAgent;
            usageCollector = new(settingsByAgent, workflowSettings.MaxLlmRequests);
            clients = clients.ToDictionary(
                pair => pair.Key,
                pair => WithTelemetryAndCheckpoints(
                    pair.Value,
                    pair.Key,
                    settingsByAgent[pair.Key],
                    usageCollector,
                    checkpointMetrics,
                    workflowId),
                StringComparer.Ordinal);
            bundle = HandoffWorkflowFactory.Create(
                clients,
                settingsByAgent,
                _workspace,
                _referenceDocuments,
                _codeGraphTools,
                codeGraphAudit,
                _checkpointStore as IToolCallStore,
                workflowId,
                _jira);
        }
        else
        {
            workflowSettings = _settings ?? throw new ConfigurationException("Workflow model settings are required.");
            IChatClient effectiveChatClient = _chatClient ?? ModelChatClientFactory.Create(workflowSettings);
            settingsByAgent = AgentNames.Required.ToDictionary(name => name, _ => workflowSettings);
            usageCollector = new(settingsByAgent, workflowSettings.MaxLlmRequests);
            Dictionary<string, IChatClient> clients = AgentNames.Required.ToDictionary(
                name => name,
                name => WithTelemetryAndCheckpoints(
                    effectiveChatClient,
                    name,
                    workflowSettings,
                    usageCollector,
                    checkpointMetrics,
                    workflowId));
            bundle = HandoffWorkflowFactory.Create(
                clients,
                settingsByAgent,
                _workspace,
                _referenceDocuments,
                _codeGraphTools,
                codeGraphAudit,
                _checkpointStore as IToolCallStore,
                workflowId,
                _jira);
        }
        string status = "stopped_without_release_approval";
        string? activeAgent = null;
        StringBuilder releaseManagerOutput = new();
        HashSet<string> observedToolCalls = new(StringComparer.Ordinal);

        yield return RunEvent.Trace(null, $"Workflow started. Waiting for the {bundle.Team.StartAgent.Name} agent.");

        await using StreamingRun run = await InProcessExecution
            .RunStreamingAsync(bundle.Workflow, task, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Handoff agents cache incoming messages and do not execute until the workflow receives
        // an explicit turn token. Without it, only HandoffStart runs and the workflow exits with
        // no agent response or handoff.
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true)).ConfigureAwait(false);

        await foreach (WorkflowEvent workflowEvent in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (workflowEvent)
            {
                case AgentResponseUpdateEvent update:
                    string? nextAgent = update.Update.AuthorName;
                    if (activeAgent is not null && nextAgent is not null &&
                        !string.Equals(activeAgent, nextAgent, StringComparison.Ordinal))
                    {
                        yield return RunEvent.Handoff(activeAgent, nextAgent);
                    }

                    activeAgent = nextAgent ?? activeAgent;
                    foreach (FunctionCallContent call in update.Update.Contents.OfType<FunctionCallContent>())
                    {
                        string identity = call.CallId ?? call.Name;
                        if (observedToolCalls.Add(identity))
                        {
                            yield return RunEvent.Tool(activeAgent, $"Requested tool: {call.Name} (call {identity}).");
                        }
                    }

                    if (!string.IsNullOrEmpty(update.Update.Text))
                    {
                        if (HandoffTopology.IsReleaseManager(activeAgent))
                        {
                            releaseManagerOutput.Append(update.Update.Text);
                            if (releaseManagerOutput.ToString().Contains(
                                HandoffTopology.CompletionMarker,
                                StringComparison.Ordinal))
                            {
                                status = "completed";
                            }
                        }

                        yield return RunEvent.Delta(nextAgent, update.Update.Text);
                    }

                    break;

                case AgentResponseEvent response:
                    string? responseAgent = response.Response.Messages.LastOrDefault()?.AuthorName
                        ?? response.Response.AgentId
                        ?? activeAgent;
                    activeAgent = responseAgent ?? activeAgent;
                    string finishReason = response.Response.FinishReason?.ToString() ?? "not reported";
                    int messageCount = response.Response.Messages.Count;
                    string contentKinds = string.Join(", ", response.Response.Messages
                        .SelectMany(message => message.Contents)
                        .Select(content => content.GetType().Name)
                        .Distinct(StringComparer.Ordinal));
                    yield return RunEvent.Trace(
                        responseAgent,
                        $"Response completed. Finish reason: {finishReason}; messages: {messageCount}; content: {(contentKinds.Length == 0 ? "none" : contentKinds)}.",
                        "response");
                    break;

                case RequestInfoEvent:
                    status = "input_required";
                    yield return RunEvent.Trace(activeAgent, "Workflow requested external input and paused autonomous execution.", "input");
                    break;

                case WorkflowOutputEvent output:
                    IEnumerable<ChatMessage>? messages = output.As<IEnumerable<ChatMessage>>();
                    if (messages is not null)
                    {
                        ChatMessage[] transcript = [.. messages];
                        bool completed = transcript.Any(message =>
                            HandoffTopology.IsCompletionMessage(message.AuthorName, message.Text));
                        bool requestLimitReached = transcript.Any(message =>
                            HandoffTopology.IsRequestLimitMessage(message.Text));
                        bool releaseRejected = transcript.Any(message =>
                            HandoffTopology.IsReleaseRejectionMessage(message.AuthorName, message.Text));
                        status = completed
                            ? "completed"
                            : requestLimitReached
                                ? "request_limit_reached"
                                : releaseRejected
                                    ? "rejected_by_release"
                                    : HandoffTopology.HasExceededReviewerRemediationCycles(
                                        transcript.Select(message => message.AuthorName),
                                        workflowSettings.MaxReviewerRemediationCycles)
                                        ? "reviewer_remediation_limit_reached"
                                        : transcript.Length >= workflowSettings.MaxTotalMessages
                                            ? "message_limit_reached"
                                            : status;
                        yield return RunEvent.Trace(
                            activeAgent,
                            $"Workflow output received with {transcript.Length} transcript messages.",
                            "workflow");
                    }

                    break;

                case WorkflowErrorEvent error:
                    yield return RunEvent.TokenUsage(usageCollector.Snapshot());
                    yield return RunEvent.Error(error.Exception?.Message ?? "The workflow failed.");
                    yield break;

                case ExecutorFailedEvent failed:
                    string failure = failed.Data?.GetBaseException().Message ?? "No error details were provided.";
                    yield return RunEvent.TokenUsage(usageCollector.Snapshot());
                    yield return RunEvent.Error($"Executor '{failed.ExecutorId}' failed: {failure}");
                    yield break;
            }
        }

        yield return RunEvent.TokenUsage(usageCollector.Snapshot());
        if (_checkpointStore is not null)
        {
            yield return RunEvent.Trace(
                null,
                $"LLM checkpoints: {checkpointMetrics.Hits:N0} reused; {checkpointMetrics.ProviderCalls:N0} provider calls recorded.",
                "checkpoint");
        }
        foreach (CodeGraphToolEvidence evidence in codeGraphAudit.Snapshot())
        {
            yield return RunEvent.Trace(
                evidence.Agent,
                $"CodeGraph evidence: {evidence.Tool} inspected '{evidence.Subject}'.",
                "codegraph");
        }

        yield return RunEvent.Decision(
            status == "completed"
                ? "Release manager approved the proposed change."
                : status == "rejected_by_release"
                    ? "Release manager rejected the proposed change with a concrete blocking finding."
                    : $"Workflow stopped with status: {status}.",
            status);
        yield return RunEvent.Finished(status);
    }

    private IChatClient WithTelemetryAndCheckpoints(
        IChatClient client,
        string agent,
        WorkflowSettings settings,
        TokenUsageCollector usageCollector,
        LlmCheckpointMetrics checkpointMetrics,
        string workflowId)
    {
        IChatClient provider = new CompletedToolCycleFilteringChatClient(client);
        IChatClient effective = new UsageTrackingChatClient(provider, agent, usageCollector);
        return _checkpointStore is null
            ? effective
            : new CheckpointingChatClient(effective, _checkpointStore, agent, settings, checkpointMetrics, workflowId);
    }
}
