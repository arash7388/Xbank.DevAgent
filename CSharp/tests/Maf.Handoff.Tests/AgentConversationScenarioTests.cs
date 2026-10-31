using System.Runtime.CompilerServices;
using System.Text.Json;
using Maf.Handoff.Core;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class AgentConversationScenarioTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task MergeRequestReviewRunsOnlyJiraReviewerAndRelease(bool approved, bool payloadOnly)
    {
        List<string> activated = [];
        var clients = AgentNames.All.ToDictionary(name => name,
            name => (IChatClient)new ScriptedClient((messages, options) =>
            {
                activated.Add(name);
                if (name == AgentNames.ReleaseManager)
                {
                    Assert.Empty(Handoffs(options));
                    return Text(approved ? HandoffTopology.CompletionMarker : HandoffTopology.ReleaseRejectionMarker);
                }
                Assert.Single(Handoffs(options));
                if (name == AgentNames.Jira)
                {
                    Assert.Contains("hand off directly to Reviewer", options!.Instructions);
                    return Handoff(Handoffs(options)[0], "Task requirements evidence");
                }
                Assert.Equal(AgentNames.Reviewer, name);
                Assert.Contains(messages, message => message.Text.Contains("Task requirements evidence", StringComparison.Ordinal));
                Assert.Contains("Apply applicable fixes yourself", options!.Instructions);
                ChatResponse response = Handoff(Handoffs(options)[0], approved ? HandoffTopology.ReviewApprovalMarker : "Unresolved blocker");
                if (payloadOnly)
                    response.Messages[0].Contents = response.Messages[0].Contents.OfType<FunctionCallContent>().Cast<AIContent>().ToList();
                return response;
            }));
        WorkflowSettings settings = new("https://example.test", "model", "key") { IsMergeRequestReview = true };
        var transcript = await Run(clients, workflowSettings: settings);
        Assert.Equal(AgentNames.ReviewRequired, activated);
        Assert.Equal(approved ? "completed" : "rejected_by_release", HandoffTopology.GetTerminationStatus(transcript, settings));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnabledSecurityGateRequiresAnExplicitApproval(bool securityApproves)
    {
        List<string> activated = [];
        string[] names = [.. AgentNames.Required, AgentNames.Security];
        Dictionary<string, IChatClient> clients = names.ToDictionary(name => name,
            name => (IChatClient)new ScriptedClient((_, options) =>
            {
                activated.Add(name);
                if (name == AgentNames.ReleaseManager) return Text(HandoffTopology.CompletionMarker);
                string evidence = name switch
                {
                    AgentNames.Reviewer => HandoffTopology.ReviewApprovalMarker,
                    AgentNames.Security => securityApproves ? "SECURITY_APPROVED" : "Security assessment remains incomplete.",
                    _ => "Implementation contract followed.",
                };
                return Handoff(Handoffs(options)[^1], evidence);
            }));

        ChatMessage[] transcript = await Run(clients);

        Assert.Contains(AgentNames.Security, activated);
        Assert.Equal(securityApproves,
            HandoffTopology.GetTerminationStatus(transcript, new("https://example.test", "model", "key")) == "completed");
    }

    public static IEnumerable<object[]> GateScenarios()
    {
        // Reviewer always runs; Security may be absent. Enabled gates approve or request remediation.
        foreach (bool jira in new[] { false, true })
        foreach (int reviewer in new[] { 1, 2 })
        foreach (int security in new[] { 0, 1, 2 })
        foreach (bool releaseRejects in new[] { false, true })
        {
            yield return [jira, reviewer, security, releaseRejects];
        }
    }

    [Theory]
    [MemberData(nameof(GateScenarios))]
    public async Task EnabledAgentsReceiveContextAndFollowEveryGatePath(
        bool jira, int reviewer, int security, bool releaseRejects)
    {
        List<string> expected = [];
        if (jira) expected.Add(AgentNames.Jira);
        expected.AddRange([AgentNames.Planner, AgentNames.Developer]);
        if (reviewer > 0)
        {
            expected.Add(AgentNames.Reviewer);
            if (reviewer == 2) expected.AddRange([AgentNames.Developer, AgentNames.Reviewer]);
        }
        if (security > 0)
        {
            expected.Add(AgentNames.Security);
            if (security == 2)
            {
                expected.Add(AgentNames.Developer);
                if (reviewer > 0) expected.Add(AgentNames.Reviewer);
                expected.Add(AgentNames.Security);
            }
        }
        expected.Add(AgentNames.ReleaseManager);

        List<string> actual = [];
        Dictionary<string, int> activations = [];
        Dictionary<string, IChatClient> clients = expected.Distinct().ToDictionary(
            name => name,
            name => (IChatClient)new ScriptedClient((messages, options) =>
            {
                int index = actual.Count;
                Assert.True(index < expected.Count, "An unexpected extra agent activation occurred.");
                Assert.Equal(expected[index], name);
                Assert.Contains(messages, message => message.Text.Contains("requested-change-42", StringComparison.Ordinal));
                if (index > 0)
                {
                    string previousEvidence = $"evidence-{index - 1}-{actual[^1]}";
                    Assert.Contains(messages, message => message.Text.Contains(previousEvidence, StringComparison.Ordinal));
                }

                actual.Add(name);
                activations[name] = activations.GetValueOrDefault(name) + 1;
                string evidence = $"evidence-{index}-{name}";
                AITool[] handoffs = Handoffs(options);
                if (name == AgentNames.ReleaseManager)
                {
                    Assert.Empty(handoffs);
                    Assert.Contains(messages, message => message.Text.Contains("evidence-", StringComparison.Ordinal));
                    return Text($"{evidence} " + (releaseRejects
                        ? HandoffTopology.ReleaseRejectionMarker
                        : HandoffTopology.CompletionMarker));
                }

                bool gate = name is AgentNames.Reviewer or AgentNames.Security;
                Assert.Equal(gate ? 2 : 1, handoffs.Length);
                bool returns = activations[name] == 1 &&
                    ((name == AgentNames.Reviewer && reviewer == 2) ||
                     (name == AgentNames.Security && security == 2));
                string decision = returns ? "BLOCKER-42" : name switch
                {
                    AgentNames.Reviewer => "REVIEW_APPROVED",
                    AgentNames.Security => "SECURITY_APPROVED",
                    _ => "contract-or-implementation",
                };
                return Handoff(handoffs[gate && !returns ? 1 : 0], $"{evidence} {decision}");
            }));

        ChatMessage[] transcript = await Run(clients);

        Assert.Equal(expected, actual);
        Assert.Equal(releaseRejects, transcript.Any(message =>
            HandoffTopology.IsReleaseRejectionMessage(message.AuthorName, message.Text)));
        Assert.Equal(!releaseRejects, transcript.Any(message =>
            HandoffTopology.IsCompletionMessage(message.AuthorName, message.Text)));
    }

    [Theory]
    [InlineData(AgentNames.Planner)]
    [InlineData(AgentNames.Developer)]
    [InlineData(AgentNames.Reviewer)]
    [InlineData(AgentNames.Security)]
    public async Task MixedToolAndHandoffWaitsForToolEvidenceAtEachStage(string mixedAgent)
    {
        RecordingSearch search = new();
        List<string> calls = [];
        Dictionary<string, int> counts = [];
        Dictionary<string, IChatClient> clients = new[]
        {
            AgentNames.Planner, AgentNames.Developer, AgentNames.Reviewer,
            AgentNames.Security, AgentNames.ReleaseManager,
        }.ToDictionary(name => name, name => (IChatClient)new ScriptedClient((messages, options) =>
        {
            calls.Add(name);
            counts[name] = counts.GetValueOrDefault(name) + 1;
            if (name == AgentNames.ReleaseManager) return Text(HandoffTopology.CompletionMarker);
            AITool[] handoffs = Handoffs(options);
            AITool target = handoffs[^1];
            if (name == mixedAgent && counts[name] == 1)
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent(Guid.NewGuid().ToString("N"), target.Name,
                        new Dictionary<string, object?> { ["reasonForHandoff"] = "premature" }),
                    new FunctionCallContent(Guid.NewGuid().ToString("N"), AgentToolCatalog.SearchDocuments.Name,
                        new Dictionary<string, object?> { ["query"] = "requirements", ["maxResults"] = 3 }),
                ]));
            }
            if (name == mixedAgent)
            {
                Assert.Equal(1, search.CallCount);
                Assert.Contains(messages, message => message.Role == ChatRole.Tool &&
                    message.Contents.OfType<FunctionResultContent>().Any(result =>
                        result.Result?.ToString()?.Contains("tool-evidence-42", StringComparison.Ordinal) is true));
            }
            return Handoff(target, name == AgentNames.Reviewer ? "REVIEW_APPROVED evidence consumed" : "evidence consumed");
        }));

        ChatMessage[] transcript = await Run(clients, referenceDocuments: search);

        Assert.Equal(1, search.CallCount);
        Assert.Equal(2, counts[mixedAgent]);
        Assert.Equal(new[] { AgentNames.Planner, AgentNames.Developer, AgentNames.Reviewer,
            AgentNames.Security, AgentNames.ReleaseManager }.SelectMany(name =>
                name == mixedAgent ? new[] { name, name } : new[] { name }), calls);
        Assert.Contains(transcript, message =>
            HandoffTopology.IsCompletionMessage(message.AuthorName, message.Text));
    }

    [Theory]
    [InlineData(AgentNames.Reviewer)]
    [InlineData(AgentNames.Security)]
    public async Task ConflictingGateHandoffsAreReconsideredBeforeOwnershipChanges(string gate)
    {
        List<string> actual = [];
        int gateCalls = 0;
        Dictionary<string, IChatClient> clients = new[]
        {
            AgentNames.Planner, AgentNames.Developer, AgentNames.Reviewer, gate, AgentNames.ReleaseManager,
        }.Distinct().ToDictionary(name => name, name => (IChatClient)new ScriptedClient((messages, options) =>
        {
            actual.Add(name);
            if (name == AgentNames.ReleaseManager) return Text(HandoffTopology.CompletionMarker);
            AITool[] handoffs = Handoffs(options);
            if (name == gate && ++gateCalls == 1)
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    handoffs.Select(tool => (AIContent)new FunctionCallContent(
                        Guid.NewGuid().ToString("N"), tool.Name,
                        new Dictionary<string, object?> { ["reasonForHandoff"] = "conflicting decision" })).ToList()));
            }
            if (name == gate)
            {
                Assert.Contains(messages, message => message.Text.Contains("choose exactly one", StringComparison.Ordinal));
            }
            return Handoff(handoffs[^1], name == AgentNames.Reviewer ? "REVIEW_APPROVED gate approved after reconsideration" : "gate approved after reconsideration");
        }));

        ChatMessage[] transcript = await Run(clients);

        Assert.Equal(new[] { AgentNames.Planner, AgentNames.Developer, AgentNames.Reviewer }
            .Concat(gate == AgentNames.Reviewer ? new[] { gate } : new[] { gate, gate })
            .Append(AgentNames.ReleaseManager), actual);
        Assert.Contains(transcript, message => HandoffTopology.IsCompletionMessage(message.AuthorName, message.Text));
    }

    [Theory]
    [InlineData(HandoffTopology.RequestLimitMarker)]
    [InlineData(HandoffTopology.ReviewerRemediationLimitMarker)]
    public async Task MarkersQuotedByUserDoNotTerminateTheConversation(string marker)
    {
        int releaseCalls = 0;
        Dictionary<string, IChatClient> clients = AgentNames.Required.ToDictionary(
            name => name, name => (IChatClient)new ScriptedClient((_, options) =>
            {
                if (name == AgentNames.ReleaseManager)
                {
                    releaseCalls++;
                    return Text(HandoffTopology.CompletionMarker);
                }
                return Handoff(Handoffs(options)[^1], name == AgentNames.Reviewer ? "REVIEW_APPROVED" : "Continue the requested change.");
            }));

        ChatMessage[] transcript = await Run(clients, task: $"requested-change-42: remove the literal {marker} from documentation.");

        Assert.Equal(1, releaseCalls);
        Assert.Contains(transcript, message =>
            HandoffTopology.IsCompletionMessage(message.AuthorName, message.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JiraConsumesIssueEvidenceBeforePassingContextToPlanner(bool unavailable)
    {
        RecordingJira jira = new(unavailable);
        int jiraCalls = 0;
        string expectedEvidence = unavailable ? "Jira data unavailable" : "issue-requirement-42";
        Dictionary<string, IChatClient> clients = AgentNames.Required.Append(AgentNames.Jira).ToDictionary(
            name => name, name => (IChatClient)new ScriptedClient((messages, options) =>
            {
                if (name == AgentNames.ReleaseManager) return Text(HandoffTopology.CompletionMarker);
                AITool handoff = Handoffs(options)[^1];
                if (name == AgentNames.Jira)
                {
                    Assert.Equal(7, options!.Tools!.Count);
                    Assert.DoesNotContain(options.Tools, tool => tool.Name.Contains("repository", StringComparison.Ordinal));
                    if (++jiraCalls == 1)
                    {
                        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [
                            new FunctionCallContent("jira-read", AgentToolCatalog.GetJiraIssueDetails.Name,
                                new Dictionary<string, object?> { ["issueKey"] = "TEST-42" }),
                            new FunctionCallContent("jira-premature-handoff", handoff.Name,
                                new Dictionary<string, object?> { ["reasonForHandoff"] = "premature" }),
                        ]));
                    }
                    FunctionResultContent result = Assert.Single(messages.SelectMany(message => message.Contents)
                        .OfType<FunctionResultContent>(), content => content.CallId == "jira-read");
                    if (unavailable)
                    {
                        Assert.True(result.Exception is not null ||
                            result.Result?.ToString()?.Contains("Error", StringComparison.OrdinalIgnoreCase) is true);
                    }
                    else Assert.Contains(expectedEvidence, result.Result?.ToString(), StringComparison.Ordinal);
                    if (!unavailable && jiraCalls == 2)
                    {
                        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("jira-comments", AgentToolCatalog.GetJiraIssueComments.Name,
                            new Dictionary<string, object?> { ["issueKey"] = "TEST-42", ["startAt"] = 1, ["maxResults"] = 50 })]));
                    }
                    if (!unavailable)
                    {
                        FunctionResultContent comments = Assert.Single(messages.SelectMany(message => message.Contents)
                            .OfType<FunctionResultContent>(), content => content.CallId == "jira-comments");
                        Assert.Contains("preserve-existing-consumers", comments.Result?.ToString(), StringComparison.Ordinal);
                    }
                    return Handoff(handoff, expectedEvidence + (unavailable ? "" : "; comment 71: preserve-existing-consumers"));
                }
                if (name == AgentNames.Planner)
                {
                    Assert.Contains(messages, message => message.Text.Contains(expectedEvidence, StringComparison.Ordinal));
                    if (!unavailable)
                        Assert.Contains(messages, message => message.Text.Contains("preserve-existing-consumers", StringComparison.Ordinal));
                }
                return Handoff(handoff, name == AgentNames.Reviewer ? "REVIEW_APPROVED" : "implementation-contract-42");
            }));

        ChatMessage[] transcript = await Run(clients, jira: jira);

        Assert.Equal(1, jira.DetailsCalls);
        Assert.Equal(unavailable ? 2 : 3, jiraCalls);
        Assert.Equal(unavailable ? 0 : 1, jira.CommentCalls);
        Assert.Contains(transcript, message => HandoffTopology.IsCompletionMessage(message.AuthorName, message.Text));
    }

    [Fact]
    public async Task PlannerStopsRepeatedInspectionAndPassesItsPlanToDeveloper()
    {
        RecordingSearch search = new();
        int plannerCalls = 0;
        Dictionary<string, IChatClient> clients = AgentNames.Required.ToDictionary(name => name,
            name => (IChatClient)new ScriptedClient((messages, options) =>
            {
                if (name == AgentNames.ReleaseManager) return Text(HandoffTopology.CompletionMarker);
                if (name == AgentNames.Reviewer) return Handoff(Handoffs(options)[^1], "REVIEW_APPROVED");
                if (name == AgentNames.Developer)
                {
                    Assert.Contains(messages, message => message.Text.Contains("implementation-contract-42", StringComparison.Ordinal));
                    return Handoff(Assert.Single(Handoffs(options)), "implementation ready");
                }
                if (++plannerCalls == 4)
                {
                    Assert.Single(options!.Tools!);
                    Assert.Contains("TOOL ROUND BUDGET REACHED", options.Instructions, StringComparison.Ordinal);
                    return Text("implementation-contract-42: preserve Settings.cs format and change RetryLimit to 4.");
                }
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent(Guid.NewGuid().ToString("N"), AgentToolCatalog.SearchDocuments.Name,
                        new Dictionary<string, object?> { ["query"] = "requirements", ["maxResults"] = 3 })]));
            }));

        ChatMessage[] transcript = await Run(clients, referenceDocuments: search);

        Assert.Equal(4, plannerCalls);
        Assert.Equal(3, search.CallCount);
        Assert.Contains(transcript, message => HandoffTopology.IsCompletionMessage(message.AuthorName, message.Text));
    }

    [Fact]
    public async Task RepeatedSecurityReturnsStopAtGlobalRequestLimit()
    {
        WorkflowSettings settings = new("https://example.test", "scripted-model", "test-key");
        string[] names = [.. AgentNames.Required, AgentNames.Security];
        TokenUsageCollector collector = new(names.ToDictionary(name => name, _ => settings), maxLlmRequests: 8);
        int releaseCalls = 0;
        Dictionary<string, IChatClient> clients = names.ToDictionary(name => name,
            name => (IChatClient)new UsageTrackingChatClient(new ScriptedClient((_, options) =>
            {
                if (name == AgentNames.ReleaseManager)
                {
                    releaseCalls++;
                    return Text(HandoffTopology.CompletionMarker);
                }
                AITool[] handoffs = Handoffs(options);
                return Handoff(handoffs[name == AgentNames.Security ? 0 : handoffs.Length - 1],
                    name == AgentNames.Security ? "security-blocker-42" : "implementation or review complete");
            }), name, collector));

        ChatMessage[] transcript = await Run(clients);

        Assert.Equal(8, collector.Snapshot().LlmRequestCount);
        Assert.Equal(0, releaseCalls);
        Assert.Contains(transcript, message => HandoffTopology.IsRequestLimitMessage(message.Text));
        Assert.DoesNotContain(transcript, message => HandoffTopology.IsCompletionMessage(message.AuthorName, message.Text));
    }

    private static AITool[] Handoffs(ChatOptions? options) =>
        [.. options?.Tools?.Where(tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal)) ?? []];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    public async Task FactoryEnforcesRequestBudgetWithoutRunnerOrTelemetry(int limit)
    {
        int calls = 0;
        string[] names = [.. AgentNames.Required];
        Dictionary<string, IChatClient> clients = names.ToDictionary(name => name,
            name => (IChatClient)new ScriptedClient((_, options) =>
            {
                calls++;
                Assert.NotEqual(AgentNames.ReleaseManager, name);
                return Handoff(Handoffs(options)[0], "A concrete blocker remains.");
            }));
        WorkflowSettings settings = new("https://example.test", "model", "key")
        {
            MaxLlmRequests = limit,
            MaxReviewerRemediationCycles = 100,
            MaxRepeatedWorkflowStates = 100, // Exercise the request cap independently of loop detection.
        };

        ChatMessage[] transcript = await Run(clients, workflowSettings: settings);

        Assert.Equal(limit, calls);
        Assert.Equal("request_limit_reached", HandoffTopology.GetTerminationStatus(transcript, settings));
    }

    [Theory]
    [InlineData(AgentNames.Developer)]
    [InlineData(AgentNames.Reviewer)]
    [InlineData(AgentNames.Security)]
    [InlineData(AgentNames.ReleaseManager)]
    public async Task ToolOnlyLoopsStopAtAgentTurnLimit(string loopingAgent)
    {
        Dictionary<string, int> calls = [];
        Dictionary<string, IChatClient> clients = new[] { AgentNames.Planner, AgentNames.Developer,
            AgentNames.Reviewer, AgentNames.Security, AgentNames.ReleaseManager }.ToDictionary(name => name,
            name => (IChatClient)new ScriptedClient((_, options) =>
            {
                calls[name] = calls.GetValueOrDefault(name) + 1;
                if (name != loopingAgent) return Handoff(Handoffs(options)[^1], "Evidence reviewed.");
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent(Guid.NewGuid().ToString("N"), AgentToolCatalog.SearchDocuments.Name,
                        new Dictionary<string, object?> { ["query"] = "requirements", ["maxResults"] = 1 })]));
            }));
        WorkflowSettings settings = new("https://example.test", "model", "key", MaxAgentTurns: 2);

        ChatMessage[] transcript = await Run(clients, referenceDocuments: new RecordingSearch(),
            workflowSettings: settings);

        Assert.Equal(2, calls[loopingAgent]);
        Assert.Equal("agent_turn_limit_reached", HandoffTopology.GetTerminationStatus(transcript, settings));
    }

    [Fact]
    public async Task AgentTurnBudgetPersistsAcrossReviewerHandoffs()
    {
        int developerCalls = 0;
        string[] names = [.. AgentNames.Required];
        Dictionary<string, IChatClient> clients = names.ToDictionary(name => name,
            name => (IChatClient)new ScriptedClient((_, options) =>
            {
                if (name == AgentNames.Developer) developerCalls++;
                Assert.NotEqual(AgentNames.ReleaseManager, name);
                return Handoff(Handoffs(options)[0], "Fix the remaining blocker.");
            }));
        WorkflowSettings settings = new("https://example.test", "model", "key", MaxAgentTurns: 2);

        ChatMessage[] transcript = await Run(clients, workflowSettings: settings);

        Assert.Equal(2, developerCalls);
        Assert.Equal("agent_turn_limit_reached", HandoffTopology.GetTerminationStatus(transcript, settings));
    }

    [Theory]
    [InlineData(AgentNames.Developer, 4, 1)]
    [InlineData(AgentNames.Reviewer, 2, 4)]
    public async Task IndividualAgentTurnLimitAppliesAcrossToolRounds(string loopingAgent, int limit, int otherLimit)
    {
        int calls = 0;
        Dictionary<string, IChatClient> clients = AgentNames.Required.ToDictionary(name => name,
            name => (IChatClient)new ScriptedClient((_, options) =>
            {
                if (name != loopingAgent) return Handoff(Handoffs(options)[^1], "Evidence reviewed.");
                calls++;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent(Guid.NewGuid().ToString("N"), AgentToolCatalog.SearchDocuments.Name,
                        new Dictionary<string, object?> { ["query"] = "requirements", ["maxResults"] = 1 })]));
            }));
        WorkflowSettings settings = new("https://example.test", "model", "key", MaxAgentTurns: otherLimit)
        { MaxRepeatedWorkflowStates = 10 };
        Dictionary<string, WorkflowSettings> profiles = clients.Keys.ToDictionary(name => name,
            name => settings with { MaxAgentTurns = name == loopingAgent ? limit : otherLimit });

        ChatMessage[] transcript = await Run(clients, referenceDocuments: new RecordingSearch(),
            workflowSettings: settings, settingsByAgent: profiles);

        Assert.Equal(limit, calls);
        Assert.Equal("agent_turn_limit_reached", HandoffTopology.GetTerminationStatus(transcript, settings));
        Assert.Contains(transcript, message => message.Text.Contains($"Agent '{loopingAgent}' reached its limit of {limit}"));
    }

    private static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static ChatResponse Handoff(AITool tool, string evidence) => new(new ChatMessage(ChatRole.Assistant,
    [
        new TextContent(evidence),
        new FunctionCallContent(Guid.NewGuid().ToString("N"), tool.Name,
            new Dictionary<string, object?> { ["reasonForHandoff"] = evidence }),
    ]));

    private static async Task<ChatMessage[]> Run(
        Dictionary<string, IChatClient> clients,
        IReferenceDocumentSearch? referenceDocuments = null,
        string task = "requested-change-42",
        IJiraIssueSearch? jira = null,
        WorkflowSettings? workflowSettings = null,
        Dictionary<string, WorkflowSettings>? settingsByAgent = null)
    {
        WorkflowSettings settings = workflowSettings ?? new("https://example.test", "scripted-model", "test-key",
            MaxTotalMessages: 100, MaxAgentTurns: 15);
        WorkflowBundle bundle = HandoffWorkflowFactory.Create(clients,
            settingsByAgent ?? clients.Keys.ToDictionary(name => name, _ => settings), referenceDocuments: referenceDocuments, jira: jira);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
            bundle.Workflow, task, cancellationToken: timeout.Token);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        ChatMessage[]? transcript = null;
        await foreach (WorkflowEvent workflowEvent in run.WatchStreamAsync(timeout.Token))
        {
            Assert.IsNotType<WorkflowErrorEvent>(workflowEvent);
            Assert.IsNotType<ExecutorFailedEvent>(workflowEvent);
            if (workflowEvent is WorkflowOutputEvent output)
            {
                transcript = [.. output.As<IEnumerable<ChatMessage>>() ?? []];
            }
        }
        Assert.NotNull(transcript);
        return transcript;
    }

    private sealed class RecordingJira(bool unavailable) : IJiraIssueSearch
    {
        public int DetailsCalls { get; private set; }
        public int CommentCalls { get; private set; }
        public Task<JsonElement> GetIssueDetailsAsync(string issueKey, CancellationToken cancellationToken = default)
        {
            Assert.Equal("TEST-42", issueKey);
            DetailsCalls++;
            return unavailable ? throw new InvalidOperationException("Jira is unavailable")
                : Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    key = issueKey,
                    fields = new
                    {
                        description = "issue-requirement-42",
                        comment = new { startAt = 0, total = 2, comments = new[] { new { id = "70", body = "Initial requirements" } } },
                    },
                }));
        }
        public Task<JsonElement> GetIssueCommentsAsync(string issueKey, int startAt = 0, int maxResults = 50,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("TEST-42", issueKey);
            Assert.Equal(1, startAt);
            Assert.Equal(50, maxResults);
            CommentCalls++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                startAt, total = 2,
                comments = new[] { new { id = "71", body = "preserve-existing-consumers" } },
            }));
        }
        public Task<string?> GetIssueTitleByKeyAsync(string issueKey, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected title lookup.");
        public Task<IReadOnlyList<JiraAttachmentInfo>> GetAttachmentsAsync(string issueKey,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected attachment lookup.");
        public Task<JiraAttachmentText> ReadAttachmentAsync(string issueKey, string attachmentId, int offset = 0,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected attachment read.");
        public Task<IReadOnlyList<JiraIssueInfo>> SearchIssuesAsync(string jql, int maxResults = 10,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected issue search.");
    }

    private sealed class RecordingSearch : IReferenceDocumentSearch
    {
        public int CallCount { get; private set; }
        public Task<string> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult("tool-evidence-42");
        }
    }

    private sealed class ScriptedClient(Func<ChatMessage[], ChatOptions?, ChatResponse> next) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(next([.. messages], options));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (ChatResponseUpdate update in next([.. messages], options).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}
