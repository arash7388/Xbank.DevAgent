using System.Runtime.CompilerServices;
using Maf.Handoff.Core;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class ReviewerBuildGuardChatClientTests
{
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

    private sealed class Client(string destination) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new TextContent("REVIEW_APPROVED"), new FunctionCallContent("call-1", destination,
                new Dictionary<string, object?> { ["reasonForHandoff"] = "REVIEW_APPROVED: diff checked" })])));

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
