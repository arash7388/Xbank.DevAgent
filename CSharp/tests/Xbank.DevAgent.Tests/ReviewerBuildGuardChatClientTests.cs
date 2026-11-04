using System.Runtime.CompilerServices;
using Xbank.DevAgent.Core;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class ReviewerBuildGuardChatClientTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DependencyBlockerStopsUnlessBuildVerificationIsSkipped(bool skipBuild, bool approvalInText)
    {
        Client provider = new("forward", approvalInText);
        Dictionary<string, IChatClient> clients = AgentNames.Required.ToDictionary(name => name, _ => (IChatClient)provider);
        int builds = 0;
        clients[AgentNames.Reviewer] = new ReviewerBuildGuardChatClient(provider, _ =>
        {
            builds++;
            return Task.FromResult(RepositoryBuildResult.Failed("error NU1101: Unable to find package XBank.OAuth. No packages exist"));
        }, skipBuild);
        WorkflowSettings settings = new("https://example.test", "model", "key") { SkipBuildVerification = skipBuild };
        WorkflowBundle bundle = HandoffWorkflowFactory.Create(clients,
            AgentNames.Required.ToDictionary(name => name, _ => settings));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(bundle.Workflow, "Change the mapping", cancellationToken: timeout.Token);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        ChatMessage[] transcript = [];
        await foreach (WorkflowEvent item in run.WatchStreamAsync(timeout.Token))
        {
            Assert.IsNotType<WorkflowErrorEvent>(item);
            if (item is ExecutorFailedEvent failed) throw failed.Data ?? new InvalidOperationException("Executor failed without diagnostics.");
            if (item is WorkflowOutputEvent output) transcript = [.. output.As<IEnumerable<ChatMessage>>() ?? []];
        }

        Assert.Equal(skipBuild ? 4 : 3, provider.Calls);
        Assert.Equal(skipBuild ? 0 : 1, builds);
        Assert.Equal(skipBuild ? "completed" : "build_blocked", HandoffTopology.GetTerminationStatus(transcript, settings));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkipBuildPreservesReviewAndForwardHandoffWithoutClaimingBuildSuccess(bool streaming)
    {
        using ReviewerBuildGuardChatClient guard = new(new Client("handoff_to_gate"), _ =>
            throw new InvalidOperationException("Build must not be invoked."), skipBuildVerification: true);
        ChatResponse response = await Invoke(guard, streaming);
        FunctionCallContent call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal("handoff_to_gate", call.Name);
        Assert.Contains(HandoffTopology.ReviewApprovalMarker, response.Text);
        Assert.Contains(BuildFailureMessages.SkippedNotice, response.Text);
        Assert.Contains(BuildFailureMessages.SkippedNotice, call.Arguments!["reasonForHandoff"]!.ToString());
        Assert.DoesNotContain("BUILD_PASSED", response.Text);
        Assert.DoesNotContain(BuildFailureMessages.Marker, response.Text);
    }

    [Fact]
    public async Task SkipBuildStillReturnsConcreteReviewFindingsToDeveloper()
    {
        using ReviewerBuildGuardChatClient guard = new(new Client("handoff_to_developer"), _ =>
            throw new InvalidOperationException("Build must not be invoked."), skipBuildVerification: true);
        ChatResponse response = await Invoke(guard, false);
        Assert.Equal("handoff_to_developer", Assert.Single(response.Messages.SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingDependencyStopsWithoutApprovalOrRemediationHandoff(bool streaming)
    {
        const string evidence = "dotnet build Jobs.csproj\nerror NU1101: Unable to find package XBank.OAuth. No packages exist with this id in source(s): nuget.org";
        using ReviewerBuildGuardChatClient guard = new(new Client("handoff_to_gate"), _ =>
            Task.FromResult(RepositoryBuildResult.Failed(evidence)));

        ChatResponse response = await Invoke(guard, streaming);

        Assert.Empty(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.DoesNotContain(HandoffTopology.ReviewApprovalMarker, response.Text);
        Assert.Contains("XBank.OAuth", response.Text);
        Assert.Contains("Configure a package source", response.Text);
        Assert.Equal("build_blocked", HandoffTopology.GetTerminationStatus(response.Messages.ToArray(),
            new WorkflowSettings("https://example.test", "model", "key")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ForwardHandoffWaitsForBuildAndRoutesDiagnostics(bool succeeds, bool streaming)
    {
        int builds = 0;
        using ReviewerBuildGuardChatClient guard = new(new Client("handoff_to_gate"), token =>
        {
            builds++;
            return Task.FromResult(new RepositoryBuildResult(succeeds, succeeds ? "BUILD_PASSED" : "error CS1002: ; expected"));
        });
        ChatResponse response = await Invoke(guard, streaming);
        FunctionCallContent call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal(1, builds);
        Assert.Equal(succeeds ? "handoff_to_gate" : "handoff_to_developer", call.Name);
        Assert.Contains(succeeds ? "BUILD_PASSED" : "CS1002", response.Text);
        Assert.Contains(succeeds ? "BUILD_PASSED" : "CS1002", call.Arguments!["reasonForHandoff"]!.ToString());
        if (!succeeds) Assert.DoesNotContain(HandoffTopology.ReviewApprovalMarker, response.Text);
    }

    [Fact]
    public async Task ReviewerFindingsReturnToDeveloperWithoutBuilding()
    {
        using ReviewerBuildGuardChatClient guard = new(new Client("handoff_to_developer"), _ =>
            throw new InvalidOperationException("Review has not passed."));
        ChatResponse response = await Invoke(guard, false);
        Assert.Equal("handoff_to_developer", Assert.Single(response.Messages[0].Contents.OfType<FunctionCallContent>()).Name);
    }

    [Fact]
    public async Task EachNewApprovalBuildsAgainAfterRemediation()
    {
        int builds = 0;
        using ReviewerBuildGuardChatClient guard = new(new Client("handoff_to_gate"), _ =>
            Task.FromResult(new RepositoryBuildResult(++builds > 1, builds == 1 ? "CS1002" : "BUILD_PASSED")));
        ChatResponse failed = await Invoke(guard, false);
        ChatResponse fixedResponse = await Invoke(guard, false);
        Assert.Contains("BUILD_FAILED", failed.Text);
        Assert.Contains("BUILD_PASSED", fixedResponse.Text);
        Assert.Equal(2, builds);
    }

    [Fact]
    public async Task CancellationDoesNotForwardApproval()
    {
        using CancellationTokenSource cancellation = new();
        using ReviewerBuildGuardChatClient guard = new(new Client("handoff_to_gate"), token =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guard.GetResponseAsync([], Options(), cancellation.Token));
    }

    private static ChatOptions Options() => new()
    {
        Tools = [
            AIFunctionFactory.Create(() => "", name: "handoff_to_developer"),
            AIFunctionFactory.Create(() => "", name: "handoff_to_gate"),
        ],
    };

    private static async Task<ChatResponse> Invoke(IChatClient client, bool streaming)
    {
        if (!streaming) return await client.GetResponseAsync([], Options());
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([], Options())) updates.Add(update);
        return updates.ToChatResponse();
    }

    private sealed class Client(string destination, bool approvalInText = true) : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (destination == "forward" && options?.Tools?.Any(tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal)) is not true)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, HandoffTopology.CompletionMarker)));
            string target = destination == "forward"
                ? options!.Tools!.Last(tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal)).Name
                : destination;
            ChatMessage message = new(ChatRole.Assistant,
                [new FunctionCallContent("call-" + Calls, target,
                    new Dictionary<string, object?> { ["reasonForHandoff"] = "REVIEW_APPROVED: diff checked" })]);
            if (approvalInText) message.Contents.Insert(0, new TextContent("REVIEW_APPROVED"));
            return Task.FromResult(new ChatResponse(message));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
