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

    public HandoffWorkflowRunner(
        WorkflowSettings settings,
        RepositoryWorkspace? workspace = null,
        IChatClient? chatClient = null,
        IReferenceDocumentSearch? referenceDocuments = null)
    {
        _settings = settings;
        _workspace = workspace;
        _chatClient = chatClient;
        _referenceDocuments = referenceDocuments;
    }

    public HandoffWorkflowRunner(
        IReadOnlyDictionary<string, WorkflowSettings> settingsByAgent,
        RepositoryWorkspace? workspace = null,
        IReferenceDocumentSearch? referenceDocuments = null)
    {
        _settingsByAgent = settingsByAgent;
        _workspace = workspace;
        _referenceDocuments = referenceDocuments;
    }

    public async IAsyncEnumerable<RunEvent> RunAsync(
        string task,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task);

        WorkflowBundle bundle;
        WorkflowSettings workflowSettings;
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

            workflowSettings = _settingsByAgent[AgentNames.Intake];
            bundle = HandoffWorkflowFactory.Create(clients, _settingsByAgent, _workspace, _referenceDocuments);
        }
        else
        {
            workflowSettings = _settings ?? throw new ConfigurationException("Workflow model settings are required.");
            IChatClient effectiveChatClient = _chatClient ?? ModelChatClientFactory.Create(workflowSettings);
            bundle = HandoffWorkflowFactory.Create(effectiveChatClient, workflowSettings, _workspace, _referenceDocuments);
        }
        string status = "stopped_without_release_approval";
        string? activeAgent = null;
        StringBuilder releaseManagerOutput = new();
        HashSet<string> observedToolCalls = new(StringComparer.Ordinal);

        yield return RunEvent.Trace(null, "Workflow started. Waiting for the intake agent.");

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

                case ExecutorInvokedEvent invoked:
                    yield return RunEvent.Trace(invoked.ExecutorId, "Agent executor invoked.", "executor");
                    break;

                case ExecutorCompletedEvent completed:
                    yield return RunEvent.Trace(completed.ExecutorId, "Agent executor completed.", "executor");
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
                        status = completed
                            ? "completed"
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
                    yield return RunEvent.Error(error.Exception?.Message ?? "The workflow failed.");
                    yield break;

                case ExecutorFailedEvent failed:
                    string failure = failed.Data?.GetBaseException().Message ?? "No error details were provided.";
                    yield return RunEvent.Error($"Executor '{failed.ExecutorId}' failed: {failure}");
                    yield break;
            }
        }

        yield return RunEvent.Decision(
            status == "completed"
                ? "Release manager approved the proposed change."
                : $"Workflow stopped with status: {status}.",
            status);
        yield return RunEvent.Finished(status);
    }
}
