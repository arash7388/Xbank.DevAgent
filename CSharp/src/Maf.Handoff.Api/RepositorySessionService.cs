using System.Collections.Concurrent;
using Maf.Handoff.Core;

namespace Maf.Handoff.Api;

public sealed class RepositorySessionService(
    GitHubPullRequestClient pullRequestClient,
    ReferenceDocumentService referenceDocuments,
    CodeGraphService codeGraph,
    IConfiguration configuration)
{
    private readonly ConcurrentDictionary<Guid, SessionState> _sessions = new();
    private readonly string _workspaceRoot = Path.Combine(Path.GetTempPath(), "maf-repository-workspaces");

    public SessionSnapshot Create(CreateSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.GitHubToken))
        {
            request = request with { GitHubToken = configuration["GitHub:Token"] };
        }
        Validate(request);
        if (request.ReferenceCollectionId is Guid collectionId && !referenceDocuments.Exists(collectionId))
        {
            throw new ArgumentException("The attached document collection was not found or has expired.");
        }
        Guid id = Guid.NewGuid();
        SessionState state = new(id, request);
        if (!_sessions.TryAdd(id, state))
        {
            throw new InvalidOperationException("Unable to allocate a repository session.");
        }

        _ = Task.Run(() => ExecuteAsync(state));
        return state.Snapshot();
    }

    public SessionSnapshot Get(Guid id) => GetState(id).Snapshot();

    public void Cancel(Guid id)
    {
        SessionState state = GetState(id);
        state.Cancel();
    }

    public void Reject(Guid id)
    {
        SessionState state = GetState(id);
        state.Reject();
    }

    public async Task<SessionSnapshot> ApproveAsync(
        Guid id,
        ApproveSessionRequest request,
        CancellationToken cancellationToken)
    {
        SessionState state = GetState(id);
        RepositoryWorkspace workspace = state.BeginApproval();
        string branch = string.IsNullOrWhiteSpace(request.Branch)
            ? $"agent/change-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{id:N}"[..42]
            : request.Branch.Trim();

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

        try
        {
            if (preparation.PushRequired)
            {
                await preparation.Workspace.PushAsync(preparation.HeadBranch, cancellationToken);
                state.MarkPullRequestBranchPushed();
            }

            PullRequestResult result = await pullRequestClient.CreateAsync(
                preparation.RepositoryUrl,
                preparation.GitHubToken,
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

        return state.Snapshot();
    }

    private async Task ExecuteAsync(SessionState state)
    {
        try
        {
            CreateSessionRequest request = state.Request;
            RepositoryWorkspace workspace = await RepositoryWorkspace.CloneAsync(
                request.RepositoryUrl,
                request.BaseBranch,
                request.GitHubToken,
                _workspaceRoot,
                state.Cancellation.Token);
            state.BeginAgentRun(workspace);

            Dictionary<string, WorkflowSettings> settingsByAgent = request.Agents.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToSettings(
                    maxTotalMessages: 120,
                    maxAgentTurns: 36,
                    maxReviewerRemediationCycles: 5),
                StringComparer.Ordinal);
            IReferenceDocumentSearch? documents = request.ReferenceCollectionId is Guid collectionId
                ? referenceDocuments.GetSearch(collectionId)
                : null;
            HandoffWorkflowRunner runner = new(settingsByAgent, workspace, documents, codeGraph);
            string? terminalStatus = null;

            await foreach (RunEvent runEvent in runner.RunAsync(request.Task, state.Cancellation.Token))
            {
                state.AddEvent(runEvent);
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
                    "message_limit_reached" =>
                        "The workflow reached its conversation safety limit before release approval.",
                    "reviewer_remediation_limit_reached" =>
                        "The reviewer requested more than five developer remediation cycles. The run stopped to prevent a review loop; inspect the logged findings and start a new run with a clarified request or prompt.",
                    "input_required" =>
                        "The workflow requested additional input before release approval. Review the agent activity for the request.",
                    "stopped_without_release_approval" =>
                        "The active agent ended without handing off to an approving release manager. Review the final agent activity and retry.",
                    null => "The workflow ended without reporting a terminal status.",
                    _ => $"The workflow ended before release approval ({terminalStatus}).",
                };
                state.MarkFailed(reason);
                return;
            }

            state.ReadyForApproval(workspace.GetDiff());
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
            if (state.Request.ReferenceCollectionId is Guid collectionId)
            {
                await referenceDocuments.DeleteAsync(collectionId);
            }
        }
    }

    private SessionState GetState(Guid id) => _sessions.TryGetValue(id, out SessionState? state)
        ? state
        : throw new KeyNotFoundException("Repository session was not found.");

    private static void Validate(CreateSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RepositoryUrl) ||
            string.IsNullOrWhiteSpace(request.BaseBranch) ||
            string.IsNullOrWhiteSpace(request.Task) ||
            request.Agents is null)
        {
            throw new ArgumentException("Repository, branch, task, and agent model configurations are required.");
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
                    maxTotalMessages: 120,
                    maxAgentTurns: 36,
                    maxReviewerRemediationCycles: 5);
            }
            catch (ConfigurationException exception)
            {
                throw new ArgumentException($"Agent '{agentName}': {exception.Message}", nameof(request), exception);
            }
        }
    }

    private sealed class SessionState
    {
        private readonly Lock _lock = new();
        private readonly List<RunEvent> _events = [];
        private RepositoryWorkspace? _workspace;
        private string _status = "cloning";
        private string _diff = string.Empty;
        private string? _error;
        private TokenUsageSummary? _usage;
        private ApprovalResult? _approval;
        private PullRequestResult? _pullRequest;

        internal SessionState(Guid id, CreateSessionRequest request)
        {
            Id = id;
            Request = request;
            StartedAt = DateTimeOffset.UtcNow;
        }

        internal Guid Id { get; }

        internal CreateSessionRequest Request { get; }

        internal DateTimeOffset StartedAt { get; }

        internal CancellationTokenSource Cancellation { get; } = new(TimeSpan.FromMinutes(20));

        internal void BeginAgentRun(RepositoryWorkspace workspace)
        {
            lock (_lock)
            {
                _workspace = workspace;
                _status = "running";
            }
        }

        internal void AddEvent(RunEvent runEvent)
        {
            lock (_lock)
            {
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
                _diff = diff;
                _status = string.IsNullOrWhiteSpace(diff) ? "no_changes" : "awaiting_approval";
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
                if (_status != "awaiting_approval" || _workspace is null)
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

                if (string.IsNullOrWhiteSpace(Request.GitHubToken))
                {
                    throw new InvalidOperationException("A GitHub token is required to create a pull request.");
                }

                string title = PullRequestTitle(Request.Task);
                string body = $"""
                    Automated pull request generated by Repository Change Workbench.

                    Requested change:
                    {Request.Task[..Math.Min(Request.Task.Length, 10_000)]}

                    Generated commit: `{_approval.Commit}`
                    """;
                _status = "creating_pull_request";
                _error = null;
                _events.Add(RunEvent.Trace(
                    null,
                    $"Creating a pull request from {_approval.Branch} into {Request.BaseBranch}.",
                    "pull_request"));
                TrimEvents();
                return new(
                    _workspace,
                    !_approval.Pushed,
                    Request.RepositoryUrl,
                    Request.GitHubToken,
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
                _events.Add(RunEvent.Trace(null, $"Created pull request #{result.Number}.", "pull_request"));
                TrimEvents();
            }
        }

        internal void FailPullRequest(string error)
        {
            lock (_lock)
            {
                _error = error;
                _status = _approval is { Pushed: true } ? "pushed" : "committed";
                _events.Add(RunEvent.Trace(null, $"Pull request creation failed: {error}", "pull_request"));
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
            Cancellation.Cancel();
            MarkCancelled();
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
                _status = "cancelled";
            }
        }

        internal void MarkFailed(string error)
        {
            lock (_lock)
            {
                _status = "failed";
                _error = error;
            }
        }

        internal SessionSnapshot Snapshot()
        {
            lock (_lock)
            {
                return new(
                    Id,
                    Request.RepositoryUrl,
                    Request.BaseBranch,
                    Request.Task,
                    StartedAt,
                    _status,
                    [.. _events],
                    _diff,
                    _error,
                    _approval,
                    _pullRequest,
                    _usage);
            }
        }

        private void TrimEvents()
        {
            if (_events.Count > 500)
            {
                _events.RemoveRange(0, _events.Count - 500);
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
    Guid? ReferenceCollectionId = null);

public sealed record ApproveSessionRequest(
    string Branch,
    string CommitMessage,
    bool Push);

internal sealed record PullRequestPreparation(
    RepositoryWorkspace Workspace,
    bool PushRequired,
    string RepositoryUrl,
    string GitHubToken,
    string Title,
    string HeadBranch,
    string BaseBranch,
    string Body);

public sealed record SessionSnapshot(
    Guid Id,
    string RepositoryUrl,
    string BaseBranch,
    string Task,
    DateTimeOffset StartedAt,
    string Status,
    IReadOnlyList<RunEvent> Events,
    string Diff,
    string? Error,
    ApprovalResult? Approval,
    PullRequestResult? PullRequest,
    TokenUsageSummary? Usage);
