using Maf.Handoff.Core;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class CodeGraphPrefetchChatClientTests
{
    [Fact]
    public async Task PrefetchesGraphOnceWithoutAnotherProviderRequestAndRemovesGraphSchemas()
    {
        int graphCalls = 0;
        string? graphQuery = null;
        AIFunction getContext = AIFunctionFactory.Create(
            (Func<string, int, int, string>)((query, maxNodes, tokenBudget) =>
            {
                graphCalls++;
                graphQuery = query;
                Assert.True(query.Length <= 256);
                return $"graph:{query}:{maxNodes}:{tokenBudget}";
            }),
            name: CodeGraphToolFactory.GetContextName);
        AIFunction repositoryTool = AIFunctionFactory.Create(
            (Func<string>)(() => "status"),
            name: AgentToolCatalog.GetRepositoryStatus.Name);
        RecordingChatClient provider = new();
        CodeGraphPrefetchChatClient client = new(provider);
        ChatOptions options = new() { Tools = [getContext, repositoryTool] };
        ChatMessage[] messages = [new(ChatRole.User, "Update Services/OrderService.cs safely.")];

        await client.GetResponseAsync(messages, options);
        await client.GetResponseAsync(messages, options);

        Assert.Equal(1, graphCalls);
        Assert.Equal("OrderService", graphQuery);
        Assert.Equal(2, provider.CallCount);
        ChatMessage prefetch = Assert.Single(
            provider.Requests[0],
            message => message.Text?.Contains(
                "CodeGraph context prefetched locally", StringComparison.Ordinal) is true);
        Assert.Equal(ChatRole.User, prefetch.Role);
        Assert.Contains("OrderService", prefetch.Text, StringComparison.Ordinal);
        Assert.Empty(provider.Requests[0]
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        Assert.Empty(provider.Requests[0]
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>());
        Assert.All(provider.Options, requestOptions =>
        {
            AIFunction tool = Assert.Single(requestOptions.Tools!.OfType<AIFunction>());
            Assert.Equal(AgentToolCatalog.GetRepositoryStatus.Name, tool.Name);
        });
    }

    [Fact]
    public async Task WaitsUntilCSharpEvidenceIsVisible()
    {
        int graphCalls = 0;
        AIFunction getContext = AIFunctionFactory.Create(
            (Func<string, int, int, string>)((query, maxNodes, tokenBudget) =>
            {
                graphCalls++;
                return "graph context";
            }),
            name: CodeGraphToolFactory.GetContextName);
        RecordingChatClient provider = new();
        CodeGraphPrefetchChatClient client = new(provider);
        ChatOptions options = new() { Tools = [getContext] };

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Change the timeout styling in styles.css.")], options);
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent("search-1", "Services/TimeoutService.cs:42"),
            ])], options);

        Assert.Equal(1, graphCalls);
        Assert.Single(provider.Options[0].Tools!);
        Assert.Empty(provider.Options[1].Tools!);
    }

    private sealed class RecordingChatClient : IChatClient
    {
        internal List<ChatMessage[]> Requests { get; } = [];

        internal List<ChatOptions> Options { get; } = [];

        internal int CallCount => Requests.Count;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add([.. messages]);
            Options.Add(options ?? new ChatOptions());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
