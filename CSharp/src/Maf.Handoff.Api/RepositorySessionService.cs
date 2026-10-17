using System.Collections.Concurrent;
using System.Text.Json;
using Maf.Handoff.Core;

namespace Maf.Handoff.Api;

public sealed class RepositorySessionService(
    GitHubPullRequestClient pullRequestClient,
    GitLabMergeRequestClient mergeRequestClient,
    ReferenceDocumentService referenceDocuments,
    CogniaDocumentSearchService cogniaDocuments,
    CodeGraphService codeGraph,
    JiraService jira,
    ILlmCheckpointStore checkpointStore,
    IWorkflowRunStore workflowRunStore,
    IConfiguration configuration)
{
    private readonly ConcurrentDictionary<Guid, SessionState> _sessions = new();
    private readonly string _workspaceRoot = Path.Combine(Path.GetTempPath(), "maf-repository-workspaces");

    private int MaxAgentTurns
    {
        get
        {
            int limit = configuration.GetValue("Workflow:MaxAgentTurns", 60);
            if (limit < 1) throw new ConfigurationException("Workflow:MaxAgentTurns must be greater than zero.");
            return limit;
        }
    }

    public SessionSnapshot Create(CreateSessionRequest request)
    {
        RepositoryProvider provider = ParseProvider(request.SourceControl);
        request = request with { SourceControl = provider.ToString().ToLowerInvariant() };
        if (provider == RepositoryProvider.GitHub && string.IsNullOrWhiteSpace(request.GitHubToken))
        {
            request = request with { GitHubToken = configuration["GitHub:Token"] };
        }
        else if (provider == RepositoryProvider.GitLab && string.IsNullOrWhiteSpace(request.GitLabToken))
        {
            request = request with { GitLabToken = configuration["GitLab:Token"] };
        }
        Guid id = Guid.NewGuid();
        request = request with
        {
            Agents = request.Agents.ToDictionary(
                pair => pair.Key,
                pair => ApplyConfiguredProviderKey(pair.Value, id),
                StringComparer.Ordinal),
        };
        Validate(request);
        _ = TaskBranchName.FromTask(request.JiraIssueKey, request.Task);
        if (request.ReferenceCollectionId is Guid collectionId && !referenceDocuments.Exists(collectionId))
        {
            throw new ArgumentException("The attached document collection was not found or has expired.");
        }
        _ = request.Cognia?.NormalizeAndValidate();
        if (request.JiraIssueKey is not null &&
            !System.Text.RegularExpressions.Regex.IsMatch(request.JiraIssueKey, @"^[A-Za-z][A-Za-z0-9_]*-\d+$"))
            throw new ArgumentException("A valid Jira issue key is required for attachment selection.");
        if (request.JiraAttachmentIds is not null &&
            (request.JiraIssueKey is null || request.JiraAttachmentIds.Count > 5 ||
             request.JiraAttachmentIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 100)))
            throw new ArgumentException("Select at most 5 Jira attachments and provide their issue key.");
        request = request with { JiraAttachmentIds = request.JiraAttachmentIds?.ToArray() };
        SessionState state = new(id, request, runEvent => workflowRunStore.AppendEvent(id, runEvent));
        state.PersistChanges = () => Persist(state);
        if (!_sessions.TryAdd(id, state))
        {
            throw new InvalidOperationException("Unable to allocate a repository session.");
        }

        try
        {
            workflowRunStore.Create(id, "repository_change", SerializeWorkflowConfig(request), state.StartedAt);
            workflowRunStore.UpdateStatus(id, "cloning");
            workflowRunStore.SaveSnapshot(id, JsonSerializer.Serialize(state.Snapshot()));
        }
        catch
        {
            _sessions.TryRemove(id, out _);
            throw;
        }

        _ = Task.Run(() => ExecuteAsync(state));
        return state.Snapshot();
    }

    private AgentModelConfiguration ApplyConfiguredProviderKey(AgentModelConfiguration agent, Guid sessionId)
    {
        if (agent.Provider is null) throw new ConfigurationException("Provider definition is required.");
        string normalizedUrl = ModelProviderDefinition.NormalizeEndpoint(agent.Provider.Url);
        if (AifaRequestContext.IsAifa(new Uri(normalizedUrl)))
        {
            return agent with
            {
                ApiKey = string.IsNullOrWhiteSpace(agent.ApiKey)
                    ? configuration["Providers:AIFA:ApiKey"] ?? string.Empty : agent.ApiKey,
                AifaContext = new AifaRequestContext(
                    configuration["Providers:AIFA:UserId"] ?? "123",
                    configuration["Providers:AIFA:SessionId"] ?? sessionId.ToString("N"),
                    configuration["Providers:AIFA:RequestId"]),
            };
        }

        if (!string.IsNullOrWhiteSpace(agent.ApiKey))
        {
            return agent;
        }

        string? providerHost = Uri.TryCreate(agent.Provider?.Url, UriKind.Absolute, out Uri? endpoint)
            ? endpoint.Host
            : null;
        if (!string.Equals(providerHost, "api.deepseek.com", StringComparison.OrdinalIgnoreCase))
        {
            return agent;
        }

        return agent with { ApiKey = configuration["Providers:DeepSeek:ApiKey"] ?? string.Empty };
    }

    public SessionSnapshot Get(Guid id)
    {
        if (_sessions.TryGetValue(id, out SessionState? state)) return state.Snapshot();
        WorkflowRunLog log = workflowRunStore.GetLog(id);
        if (workflowRunStore.GetSnapshot(id) is string json)
        {
            SessionSnapshot saved = JsonSerializer.Deserialize<SessionSnapshot>(json)!;
            return saved with
            {
                Status = log.Run.Status, Error = log.Run.Error,
                Events = log.Events ?? saved.Events,
                Usage = log.Usage?.TokenUsage ?? saved.Usage,
            };
        }
        // Older databases retain configuration and execution logs, but no session snapshot.
        using JsonDocument config = JsonDocument.Parse(log.Workflow.ConfigJson);
        string Read(string name, string fallback = "") => config.RootElement.TryGetProperty(name, out JsonElement value)
            ? value.GetString() ?? fallback : fallback;
        return new(id, Read("RepositoryUrl"), Read("SourceControl", "gitlab"), Read("BaseBranch"), Read("Task"),
            log.Run.CreatedAt, log.Run.Status, log.Events ?? [], "", log.Run.Error, null, null, log.Usage?.TokenUsage);
    }

    public void Cancel(Guid id)
    {
        SessionState state = GetState(id);
        state.Cancel();
        Persist(state);
    }

    public SessionSnapshot Clarify(Guid id, ClarifySessionRequest request)
    {
        SessionState state = GetState(id);
        state.Answer(request);
        Persist(state);
        return state.Snapshot();
    }

    public void Reject(Guid id)
    {
        SessionState state = GetState(id);
        state.Reject();
        Persist(state);
    }

    public async Task<SessionSnapshot> ApproveAsync(
        Guid id,
        ApproveSessionRequest request,
        CancellationToken cancellationToken)
    {
        SessionState state = GetState(id);
        string branch = TaskBranchName.ForApproval(state.Request.JiraIssueKey, state.Request.Task, request.Branch);
        RepositoryWorkspace workspace = state.BeginApproval();
        Persist(state);

        try
        {
            ApprovalResult result = await workspace.CommitAsync(
                branch,
                request.CommitMessage.Trim(),
                request.Push,
                cancellationToken);
            state.CompleteApproval(result);
        }
        catch (Exception exception) when (exception is RepositoryException or OperationCanceledException)
        {
            state.FailApproval(exception.Message);
        }

        Persist(state);
        return state.Snapshot();
    }

    public async Task<SessionSnapshot> CreatePullRequestAsync(Guid id, CancellationToken cancellationToken)
    {
        SessionState state = GetState(id);
        PullRequestPreparation? preparation = state.BeginPullRequest();
        if (preparation is null)
        {
            return state.Snapshot();
        }
        Persist(state);

        try
        {
            if (preparation.PushRequired)
            {
                await preparation.Workspace.PushAsync(preparation.HeadBranch, cancellationToken);
                state.MarkPullRequestBranchPushed();
                Persist(state);
            }

            PullRequestResult result = preparation.Provider == RepositoryProvider.GitHub
                ? await pullRequestClient.CreateAsync(
                    preparation.RepositoryUrl,
                    preparation.AccessToken,
                    preparation.Title,
                    preparation.HeadBranch,
                    preparation.BaseBranch,
                    preparation.Body,
                    cancellationToken)
                : await mergeRequestClient.CreateAsync(
                    preparation.RepositoryUrl,
                    preparation.AccessToken,
                    preparation.Title,
                    preparation.HeadBranch,
                    preparation.BaseBranch,
                    preparation.Body,
                    cancellationToken);
            state.CompletePullRequest(result);
        }
        catch (Exception exception) when (exception is RepositoryException or OperationCanceledException)
        {
            state.FailPullRequest(exception.Message);
        }

        Persist(state);
        return state.Snapshot();
    }

    private async Task ExecuteAsync(SessionState state)
    {
        try
        {
            CreateSessionRequest request = state.Request;
            RepositoryProvider provider = ParseProvider(request.SourceControl);
            RepositoryWorkspace workspace = await RepositoryWorkspace.CloneAsync(
                request.RepositoryUrl,
                request.BaseBranch,
                AccessToken(request, provider),
                _workspaceRoot,
                state.Cancellation.Token,
                provider,
                ConfiguredGitLabHost());
            state.BeginAgentRun(workspace);
            Persist(state);

            Dictionary<string, WorkflowSettings> settingsByAgent = request.Agents.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToSettings(
                    maxTotalMessages: 100,
                    maxAgentTurns: MaxAgentTurns,
                    maxReviewerRemediationCycles: 5,
                    maxLlmRequests: request.MaxAiCalls,
                    maxBudgetUsd: request.MaxBudgetUsd) with
                {
                    MaxRepeatedWorkflowStates = configuration.GetValue("Workflow:MaxRepeatedStates", 3),
                    SkipBuildVerification = request.SkipBuildVerification,
                    AutonomyLevel = request.AutonomyLevel,
                },
                StringComparer.Ordinal);
            IReferenceDocumentSearch? documents = CreateDocumentSearch(request);
            IWorkflowUserInput decisions = new AutonomyUserInput(request.AutonomyLevel, state, questions =>
            {
                state.AddEvent(new("autonomy_decision", Text: questions + "\nDecision delegated to agents; no human answer requested.", Category: "input"));
                Persist(state);
            });
            state.AddEvent(new("autonomy_policy", Text: $"Autonomy: {request.AutonomyLevel}%. Automatic commit: {request.AutoCommit}; push: {request.AutoPush}; PR/MR: {request.AutoCreatePullRequest}.", Category: "input"));
            IJiraIssueSearch? workflowJira = null;
            string workflowTask = request.Task;
            if (request.JiraIssueKey is not null)
            {
                workflowTask = $"Selected Jira issue: {request.JiraIssueKey}\n\n{workflowTask}";
            }
            if (settingsByAgent.ContainsKey(AgentNames.Jira))
            {
                workflowJira = jira;
                if (request.JiraIssueKey is not null)
                {
                    SelectedJiraAttachments selectedAttachments = new(jira, request.JiraIssueKey, request.JiraAttachmentIds ?? []);
                    workflowJira = selectedAttachments;
                    workflowTask += await selectedAttachments.LoadContextAsync(state.Cancellation.Token);
                }
                workflowJira = new SelectedJiraComments(workflowJira, request.JiraIncludeComments);
                if (!request.JiraIncludeComments)
                    workflowTask += "\nJira comment selection: The user excluded Jira comments from this run. Do not read or use issue comments; this intentional exclusion is not an evidence gap or a problem to report.\n";
                workflowJira = new WorkflowJiraContext(workflowJira, jira,
                    WorkflowJiraContext.ResolveIssueKey(request.JiraIssueKey, request.Task), runEvent =>
                    {
                        state.AddEvent(runEvent);
                        Persist(state);
                    }, decisions);
            }
            HandoffWorkflowRunner runner = new(
                settingsByAgent,
                workspace,
                documents,
                request.CodeGraphEnabled && codeGraph.IsWorkflowEnabled ? codeGraph : null,
                checkpointStore,
                workflowJira,
                decisions);
            string? terminalStatus = null;

            await foreach (RunEvent runEvent in runner.RunAsync(
                workflowTask,
                state.Cancellation.Token,
                state.Id.ToString("D")))
            {
                state.AddEvent(runEvent);
                Persist(state);
                if (runEvent.Type == "error")
                {
                    state.MarkFailed(runEvent.Text ?? "The agent workflow failed.");
                    return;
                }

                if (runEvent.Type == "run_finished")
                {
                    terminalStatus = runEvent.Status;
                }
            }

            if (!string.Equals(terminalStatus, "completed", StringComparison.Ordinal))
            {
                string reason = terminalStatus switch
                {
                    "build_blocked" => "Build verification is blocked by an unavailable build prerequisite. The proposed changes are preserved and have not been approved.",
                    "message_limit_reached" =>
                        "The workflow reached its conversation safety limit before release approval.",
                    "reviewer_remediation_limit_reached" =>
                        "The reviewer exceeded the configured developer remediation limit. Inspect the logged findings and start a new run with a clarified request or prompt.",
                    "loop_detected" =>
                        "The workflow repeated the same repository and tool evidence after a role recovery attempt. Further model calls were stopped. The patch is preserved; inspect the loop details before starting a new run.",
                    "budget_limit_reached" =>
                        "The workflow reached its configured AI budget before release approval.",
                    "request_limit_reached" =>
                        "The workflow reached its model request limit before release approval, including checkpoint replays. Review the final agent activity.",
                    "agent_turn_limit_reached" =>
                        "An agent reached its model turn limit before release approval, including tool rounds and repeated activations. Review the final agent activity.",
                    "rejected_by_release" =>
                        "The release manager rejected the patch. Review its concrete blocking finding before starting a corrected run.",
                    "review_approval_missing" =>
                        "The mandatory reviewer did not confirm the latest implementation with REVIEW_APPROVED. The patch cannot proceed to approval.",
                    "input_required" =>
                        "The workflow requested additional input before release approval. Review the agent activity for the request.",
                    "stopped_without_release_approval" =>
                        "The active agent ended without handing off to an approving release manager. Review the final agent activity and retry.",
                    null => "The workflow ended without reporting a terminal status.",
                    _ => $"The workflow ended before release approval ({terminalStatus}).",
                };
                state.MarkFailed(reason, workspace.GetDiff());
                return;
            }

            state.ReadyForApproval(workspace.GetDiff());
            await CompleteReviewedChangesAsync(state);
        }
        catch (OperationCanceledException)
        {
            state.MarkCancelled();
        }
        catch (Exception exception) when (
            exception is RepositoryException or ConfigurationException or InvalidOperationException)
        {
            state.MarkFailed(exception.Message);
        }
        catch (Exception)
        {
            state.MarkFailed("The repository workflow failed unexpectedly. Check server logs.");
        }
        finally
        {
            state.FinishAutomaticCompletion();
            Persist(state);
            if (state.Request.ReferenceCollectionId is Guid collectionId)
            {
                await referenceDocuments.DeleteAsync(collectionId);
            }
        }
    }

    private async Task CompleteReviewedChangesAsync(SessionState state)
    {
        CreateSessionRequest request = state.Request;
        if (state.Snapshot().Status != "awaiting_approval" || !request.AutoCommit) return;
        string branch = TaskBranchName.FromTask(request.JiraIssueKey, request.Task);
        await ApproveAsync(state.Id, new(branch, $"Implement {branch}", request.AutoPush), state.Cancellation.Token);
        if (request.AutoCreatePullRequest && state.Snapshot().Status is "committed" or "pushed")
            await CreatePullRequestAsync(state.Id, state.Cancellation.Token);
    }

    private void Persist(SessionState state)
    {
        SessionSnapshot snapshot = state.Snapshot();
        workflowRunStore.UpdateStatus(snapshot.Id, snapshot.Status, snapshot.Error);
        workflowRunStore.SaveSnapshot(snapshot.Id, JsonSerializer.Serialize(snapshot));
    }

    private string SerializeWorkflowConfig(CreateSessionRequest request) => JsonSerializer.Serialize(new
    {
        request.RepositoryUrl,
        request.BaseBranch,
        request.Task,
        request.ReferenceCollectionId,
        request.JiraIssueKey,
        request.JiraAttachmentIds,
        request.JiraIncludeComments,
        request.SkipBuildVerification,
        request.AutonomyLevel,
        request.AutoCommit,
        request.AutoPush,
        request.AutoCreatePullRequest,
        cognia = request.Cognia is null ? null : new
        {
            request.Cognia.Endpoint,
            request.Cognia.KnowledgeBaseIds,
            clientIdConfigured = !string.IsNullOrWhiteSpace(request.Cognia.ClientId),
            clientSecretConfigured = !string.IsNullOrWhiteSpace(request.Cognia.ClientSecret),
        },
        request.CodeGraphEnabled,
        githubTokenConfigured = !string.IsNullOrWhiteSpace(request.GitHubToken),
        gitlabTokenConfigured = !string.IsNullOrWhiteSpace(request.GitLabToken),
        request.SourceControl,
        agents = request.Agents.ToDictionary(
            pair => pair.Key,
            pair => new
            {
                pair.Value.Provider,
                pair.Value.Prompt,
                apiKeyConfigured = !string.IsNullOrWhiteSpace(pair.Value.ApiKey),
            },
            StringComparer.Ordinal),
        limits = new
        {
            maxTotalMessages = 100,
            maxAgentTurns = MaxAgentTurns,
            maxReviewerRemediationCycles = 5,
            maxRepeatedWorkflowStates = configuration.GetValue("Workflow:MaxRepeatedStates", 3),
            maxLlmRequests = request.MaxAiCalls,
            maxBudgetUsd = request.MaxBudgetUsd,
        },
    });

    private IReferenceDocumentSearch? CreateDocumentSearch(CreateSessionRequest request)
    {
        List<(string Name, IReferenceDocumentSearch Search)> sources = [];
        if (request.ReferenceCollectionId is Guid collectionId)
        {
            sources.Add(("Attached documents", referenceDocuments.GetSearch(collectionId)));
        }

        if (request.Cognia is not null)
        {
            sources.Add(("Cognia organization knowledge", cogniaDocuments.CreateSearch(request.Cognia)));
        }

        return sources.Count switch
        {
            0 => null,
            1 => sources[0].Search,
            _ => new CompositeReferenceDocumentSearch(sources),
        };
    }

    private SessionState GetState(Guid id) => _sessions.TryGetValue(id, out SessionState? state)
        ? state
        : throw new KeyNotFoundException("Repository session was not found.");

    private void Validate(CreateSessionRequest request)
    {
        if (!AutonomyPolicy.IsValid(request.AutonomyLevel))
            throw new ArgumentException("Autonomy must be 0, 25, 50, 75, or 100.");
        if (request.AutoPush && !request.AutoCommit || request.AutoCreatePullRequest && !request.AutoPush)
            throw new ArgumentException("Automatic push requires automatic commit; automatic PR/MR creation requires automatic push.");
        _ = ParseProvider(request.SourceControl);
        if (string.IsNullOrWhiteSpace(request.RepositoryUrl) ||
            string.IsNullOrWhiteSpace(request.BaseBranch) ||
            string.IsNullOrWhiteSpace(request.Task) ||
            request.Agents is null)
        {
            throw new ArgumentException("Repository, branch, task, and agent model configurations are required.");
        }

        if (request.MaxAiCalls < 1 || request.MaxBudgetUsd < 0.01m || request.MaxBudgetUsd > 1_000_000m)
        {
            throw new ArgumentException("Maximum AI calls must be positive and the USD budget must be between 0.01 and 1,000,000.");
        }

        if (request.Task.Length > 20_000)
        {
            throw new ArgumentException("Change request must be 20,000 characters or fewer.");
        }

        foreach (string unexpectedAgent in request.Agents.Keys.Except(AgentNames.All, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown agent configuration '{unexpectedAgent}'.");
        }

        foreach (string agentName in AgentNames.Required)
        {
            if (!request.Agents.TryGetValue(agentName, out AgentModelConfiguration? configuration) ||
                configuration is null)
            {
                throw new ArgumentException($"Model configuration for agent '{agentName}' is required.");
            }

            try
            {
                configuration.ToSettings(
                    maxTotalMessages: 100,
                    maxAgentTurns: MaxAgentTurns,
                    maxReviewerRemediationCycles: 5,
                    maxLlmRequests: request.MaxAiCalls,
                    maxBudgetUsd: request.MaxBudgetUsd);
            }
            catch (ConfigurationException exception)
            {
                throw new ArgumentException($"Agent '{agentName}': {exception.Message}", nameof(request), exception);
            }
        }
    }

    private static RepositoryProvider ParseProvider(string? sourceControl) =>
        sourceControl?.Trim().ToLowerInvariant() switch
        {
            null or "" or "gitlab" => RepositoryProvider.GitLab,
            "github" => RepositoryProvider.GitHub,
            _ => throw new ArgumentException("Source control must be either 'github' or 'gitlab'."),
        };

    private static string? AccessToken(CreateSessionRequest request, RepositoryProvider provider) =>
        provider == RepositoryProvider.GitHub ? request.GitHubToken : request.GitLabToken;

    private string? ConfiguredGitLabHost()
    {
        string? configured = configuration["GitLab:Host"];
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    private sealed class SessionState : IWorkflowUserInput
    {
        private readonly Lock _lock = new();
        private readonly List<RunEvent> _events = [];
        private readonly Action<RunEvent> _persistEvent;
        private RepositoryWorkspace? _workspace;
        private string _status = "cloning";
        private string _diff = string.Empty;
        private string? _error;
        private TokenUsageSummary? _usage;
        private ApprovalResult? _approval;
        private PullRequestResult? _pullRequest;
        private TaskCompletionSource<string>? _answer;
        private SessionClarification? _clarification;
        private bool _automaticCompletionPending;
        internal Action? PersistChanges { get; set; }

        internal SessionState(Guid id, CreateSessionRequest request, Action<RunEvent> persistEvent)
        {
            Id = id;
            Request = request;
            _persistEvent = persistEvent;
            StartedAt = DateTimeOffset.UtcNow;
        }

        internal Guid Id { get; }

        internal CreateSessionRequest Request { get; private set; }

        internal DateTimeOffset StartedAt { get; }

        internal CancellationTokenSource Cancellation { get; } = new(TimeSpan.FromMinutes(50));

        public async Task<string> AskAsync(string questions, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(questions);
            cancellationToken.ThrowIfCancellationRequested();
            if (Request.AutonomyLevel == 100)
            {
                AddEvent(new("autonomy_decision", Text: questions + "\nDecision delegated to agents; no human answer requested.", Category: "input"));
                PersistChanges?.Invoke();
                return AutonomyPolicy.DelegatedDecision;
            }
            TaskCompletionSource<string> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_status != "running" || _answer is not null)
                    throw new InvalidOperationException("The run cannot request clarification in its current state.");
                _answer = answer;
                _clarification = new(Guid.NewGuid(), questions);
                _status = "awaiting_input";
                // Human response time must not consume the autonomous execution timeout.
                Cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
                AddEvent(new("clarification_requested", Text: questions,
                    Source: _clarification.Id.ToString("D"), Status: "awaiting_input", Category: "input"));
            }
            try
            {
                PersistChanges?.Invoke();
                return await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_answer, answer))
                    {
                        _answer = null;
                        _clarification = null;
                    }
                }
            }
        }

        internal void Answer(ClarifySessionRequest request)
        {
            string answer = request.Answer?.Trim() ?? "";
            if (answer.Length is 0 or > 12000)
                throw new ArgumentException("Provide a clarification answer between 1 and 12000 characters.");
            lock (_lock)
            {
                Cancellation.Token.ThrowIfCancellationRequested();
                if (_status != "awaiting_input" || _clarification?.Id != request.ClarificationId || _answer is null)
                    throw new InvalidOperationException("This clarification is no longer pending.");
                AddEvent(new("clarification_answered", Text: answer,
                    Source: request.ClarificationId.ToString("D"), Status: "running", Category: "input"));
                TaskCompletionSource<string> pending = _answer;
                _answer = null;
                _clarification = null;
                _status = "running";
                Cancellation.CancelAfter(TimeSpan.FromMinutes(50));
                pending.TrySetResult(answer);
            }
        }

        internal void BeginAgentRun(RepositoryWorkspace workspace)
        {
            lock (_lock)
            {
                if (_status == "cancelled") throw new OperationCanceledException();
                Cancellation.Token.ThrowIfCancellationRequested();
                _workspace = workspace;
                Request = Request with { BaseBranch = workspace.BaseBranch };
                _status = "running";
            }
        }

        internal void AddEvent(RunEvent runEvent)
        {
            lock (_lock)
            {
                _persistEvent(runEvent);
                if (runEvent.Type == "agent_summary")
                {
                    _events.RemoveAll(previous => previous.Type == "agent_summary" && previous.Agent == runEvent.Agent);
                }
                if (runEvent.Type == "jira_problem")
                    _events.RemoveAll(previous => previous.Type == "jira_problem");
                if (runEvent.Usage is not null)
                {
                    _usage = runEvent.Usage;
                }

                if (runEvent.Type == "message" && _events.LastOrDefault() is RunEvent previous &&
                    previous.Type == "message" && previous.Agent == runEvent.Agent)
                {
                    _events[^1] = previous with { Text = (previous.Text ?? string.Empty) + runEvent.Text };
                }
                else
                {
                    _events.Add(runEvent);
                }

                TrimEvents();
            }
        }

        internal void ReadyForApproval(string diff)
        {
            lock (_lock)
            {
                if (_status == "cancelled") return;
                _diff = diff;
                _status = string.IsNullOrWhiteSpace(diff) ? "no_changes"
                    : Request.AutonomyLevel == 100 && !Request.AutoCommit ? "completed" : "awaiting_approval";
                _automaticCompletionPending = Request.AutoCommit && !string.IsNullOrWhiteSpace(diff);
                if (_status == "no_changes")
                {
                    _error = "The workflow completed without changing source files.";
                }
            }
        }

        internal RepositoryWorkspace BeginApproval()
        {
            lock (_lock)
            {
                if (_status is not ("awaiting_approval" or "completed") || _workspace is null)
                {
                    throw new InvalidOperationException("This session is not awaiting approval.");
                }

                _status = "approving";
                return _workspace;
            }
        }

        internal void CompleteApproval(ApprovalResult result)
        {
            lock (_lock)
            {
                _approval = result;
                _status = result.Pushed ? "pushed" : "committed";
            }
        }

        internal PullRequestPreparation? BeginPullRequest()
        {
            lock (_lock)
            {
                if (_pullRequest is not null)
                {
                    return null;
                }

                if (_status is not ("committed" or "pushed") || _approval is null || _workspace is null)
                {
                    throw new InvalidOperationException("An approved agent branch is required before creating a pull request.");
                }

                RepositoryProvider provider = ParseProvider(Request.SourceControl);
                string? accessToken = AccessToken(Request, provider);
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    string requiredRequestName = provider == RepositoryProvider.GitHub ? "pull request" : "merge request";
                    throw new InvalidOperationException(
                        $"A {provider.DisplayName()} token is required to create a {requiredRequestName}.");
                }

                string title = PullRequestTitle(Request.Task);
                string requestName = provider == RepositoryProvider.GitHub ? "pull request" : "merge request";
                string body = $"""
                    Automated {requestName} generated by Repository Change Workbench.

                    Requested change:
                    {Request.Task[..Math.Min(Request.Task.Length, 10_000)]}

                    Generated commit: `{_approval.Commit}`
                    """;
                _status = "creating_pull_request";
                _error = null;
                AddEvent(RunEvent.Trace(
                    null,
                    $"Creating a {requestName} from {_approval.Branch} into {Request.BaseBranch}.",
                    "pull_request"));
                TrimEvents();
                return new(
                    _workspace,
                    !_approval.Pushed,
                    Request.RepositoryUrl,
                    provider,
                    accessToken,
                    title,
                    _approval.Branch,
                    Request.BaseBranch,
                    body);
            }
        }

        internal void MarkPullRequestBranchPushed()
        {
            lock (_lock)
            {
                if (_approval is not null)
                {
                    _approval = _approval with { Pushed = true };
                }
            }
        }

        internal void CompletePullRequest(PullRequestResult result)
        {
            lock (_lock)
            {
                _pullRequest = result;
                _status = "pull_request_created";
                RepositoryProvider provider = ParseProvider(Request.SourceControl);
                string requestName = provider == RepositoryProvider.GitHub ? "pull request" : "merge request";
                AddEvent(RunEvent.Trace(null, $"Created {requestName} #{result.Number}.", "pull_request"));
                TrimEvents();
            }
        }

        internal void FailPullRequest(string error)
        {
            lock (_lock)
            {
                _error = error;
                _status = _approval is { Pushed: true } ? "pushed" : "committed";
                RepositoryProvider provider = ParseProvider(Request.SourceControl);
                string requestName = provider == RepositoryProvider.GitHub ? "Pull request" : "Merge request";
                AddEvent(RunEvent.Trace(null, $"{requestName} creation failed: {error}", "pull_request"));
                TrimEvents();
            }
        }

        internal void FailApproval(string error)
        {
            lock (_lock)
            {
                _error = error;
                _status = "approval_failed";
            }
        }

        internal void Cancel()
        {
            lock (_lock)
            {
                if (_status == "cancelled") return;
                if (_status is not ("cloning" or "running" or "awaiting_input"))
                    throw new InvalidOperationException("Only an active agent run can be stopped.");
                _status = "cancelled";
                _clarification = null;
            }
            Cancellation.Cancel();
        }

        internal void Reject()
        {
            lock (_lock)
            {
                if (_status != "awaiting_approval")
                {
                    throw new InvalidOperationException("Only a pending patch can be rejected.");
                }

                _status = "rejected";
            }
        }

        internal void MarkCancelled()
        {
            lock (_lock)
            {
                if (_status is not ("cloning" or "running" or "awaiting_input" or "cancelled")) return;
                _status = "cancelled";
                _clarification = null;
            }
        }

        internal void MarkFailed(string error, string? diff = null)
        {
            lock (_lock)
            {
                if (_status == "cancelled") return;
                _status = "failed";
                _error = error;
                if (diff is not null) _diff = diff;
            }
        }

        internal SessionSnapshot Snapshot()
        {
            lock (_lock)
            {
                return new(
                    Id,
                    Request.RepositoryUrl,
                    Request.SourceControl,
                    Request.BaseBranch,
                    Request.Task,
                    StartedAt,
                    _status,
                    [.. _events],
                    _diff,
                    _error,
                    _approval,
                    _pullRequest,
                    _usage,
                    TaskBranchName.FromTask(Request.JiraIssueKey, Request.Task),
                    _clarification,
                    Request.AutonomyLevel,
                    _automaticCompletionPending);
            }
        }

        internal void FinishAutomaticCompletion()
        {
            lock (_lock) _automaticCompletionPending = false;
        }

        private void TrimEvents()
        {
            if (_events.Count > 500)
            {
                int excess = _events.Count - 500;
                _events.RemoveAll(runEvent => runEvent.Type is not ("agent_summary" or "jira_problem") && excess-- > 0);
            }
        }

        private static string PullRequestTitle(string task)
        {
            string title = task.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim() ?? "Agent-generated repository change";
            return title.Length <= 120 ? title : title[..117] + "...";
        }
    }
}

public sealed record CreateSessionRequest(
    string RepositoryUrl,
    string BaseBranch,
    string Task,
    IReadOnlyDictionary<string, AgentModelConfiguration> Agents,
    string? GitHubToken,
    Guid? ReferenceCollectionId = null,
    bool CodeGraphEnabled = true,
    string? GitLabToken = null,
    string SourceControl = "gitlab",
    CogniaSearchConfiguration? Cognia = null,
    string? JiraIssueKey = null,
    IReadOnlyList<string>? JiraAttachmentIds = null,
    int MaxAiCalls = 30,
    decimal MaxBudgetUsd = 0.10m,
    bool JiraIncludeComments = true,
    bool SkipBuildVerification = false,
    int AutonomyLevel = 100,
    bool AutoCommit = false,
    bool AutoPush = false,
    bool AutoCreatePullRequest = false);

public sealed record ProjectRepositoryInfo(string Key, string? GitLabProjectPath);

public sealed record ApproveSessionRequest(
    string Branch,
    string CommitMessage,
    bool Push);

public sealed record SessionClarification(Guid Id, string Questions);
public sealed record ClarifySessionRequest(Guid ClarificationId, string Answer);

internal sealed record PullRequestPreparation(
    RepositoryWorkspace Workspace,
    bool PushRequired,
    string RepositoryUrl,
    RepositoryProvider Provider,
    string AccessToken,
    string Title,
    string HeadBranch,
    string BaseBranch,
    string Body);

public sealed record SessionSnapshot(
    Guid Id,
    string RepositoryUrl,
    string SourceControl,
    string BaseBranch,
    string Task,
    DateTimeOffset StartedAt,
    string Status,
    IReadOnlyList<RunEvent> Events,
    string Diff,
    string? Error,
    ApprovalResult? Approval,
    PullRequestResult? PullRequest,
    TokenUsageSummary? Usage,
    string? Branch = null,
    SessionClarification? Clarification = null,
    int AutonomyLevel = 100,
    bool AutomaticCompletionPending = false)
{
    // Derived from the durable event history too, so existing archived runs gain full diagnostics.
    public SessionFailureDetails? FailureDetails => SessionFailureDetails.FromEvents(Error, Events);
}
