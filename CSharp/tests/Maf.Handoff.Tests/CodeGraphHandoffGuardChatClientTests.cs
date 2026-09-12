using System.Runtime.CompilerServices;
using Maf.Handoff.Core;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class CodeGraphHandoffGuardChatClientTests
{
    [Fact]
    public async Task RecordsEvidenceOnlyAfterSuccessfulRelationshipToolExecutionAsync()
    {
        FakeCodeGraphTools provider = new();
        CodeGraphAudit audit = new();
        AIFunction function = Assert.IsAssignableFrom<AIFunction>(CodeGraphToolFactory.Create(
            provider,
            audit,
            AgentNames.Reviewer).Single(tool => tool.Name == CodeGraphToolFactory.GetContextName));

        await function.InvokeAsync(new AIFunctionArguments
        {
            ["query"] = "OrderService",
            ["maxNodes"] = 10,
            ["tokenBudget"] = 1_500,
        });

        Assert.True(audit.TryConsumeRelationshipCheck(AgentNames.Reviewer));
        Assert.Equal("OrderService", Assert.Single(audit.Snapshot()).Subject);
    }

    [Fact]
    public async Task BlocksHandoffUntilRelationshipEvidenceExistsAsync()
    {
        SequenceChatClient inner = new(HandoffResponse("handoff-1"), HandoffResponse("handoff-2"));
        CodeGraphAudit audit = new();
        using CodeGraphHandoffGuardChatClient guard = new(inner, AgentNames.Developer, audit);

        ChatResponse blocked = await guard.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Implement the C# change.")]);
        audit.Record(AgentNames.Developer, CodeGraphToolFactory.GetContextName, "OrderService");
        ChatResponse allowed = await guard.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hand off the verified change.")]);

        Assert.Contains("CodeGraph policy", blocked.Text, StringComparison.Ordinal);
        Assert.Single(allowed.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
    }

    [Fact]
    public async Task RequiresFreshEvidenceForEveryHandoffAsync()
    {
        SequenceChatClient inner = new(HandoffResponse("handoff-1"), HandoffResponse("handoff-2"));
        CodeGraphAudit audit = new();
        audit.Record(AgentNames.Developer, CodeGraphToolFactory.GetNeighborsName, "symbol-id");
        using CodeGraphHandoffGuardChatClient guard = new(inner, AgentNames.Developer, audit);

        ChatResponse first = await guard.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "First handoff.")]);
        ChatResponse second = await guard.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Second handoff.")]);

        Assert.Single(first.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Contains("CodeGraph policy", second.Text, StringComparison.Ordinal);
    }

    private static ChatResponse HandoffResponse(string callId) => new(new ChatMessage(
        ChatRole.Assistant,
        [new FunctionCallContent(callId, "handoff_to_next", new Dictionary<string, object?>())]));

    private sealed class SequenceChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(_responses.Dequeue());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (ChatResponseUpdate update in _responses.Dequeue().ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeCodeGraphTools : ICodeGraphTools
    {
        public void EnsureAvailable(string repositoryUrl)
        {
        }

        public string FindSymbol(string name, int limit = 15) => "[]";

        public string GetNeighbors(string id, int limit = 100) => "[{\"kind\":\"CALLS\"}]";

        public string GetContext(string query, int maxNodes = 15, int tokenBudget = 2_500) =>
            $"# Context: {query}";

        public string GetContextById(string id, int maxNodes = 15, int tokenBudget = 2_500) =>
            $"# Context: {id}";
    }
}
