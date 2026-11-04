using Xbank.DevAgent.Core;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class CodeGraphPrefetchChatClientTests
{
    [Fact]
    public async Task PrefetchesGraphOnceAndRetainsContextAndGraphToolsAcrossRequests()
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
            Assert.Same(options, requestOptions);
            Assert.Contains(getContext, requestOptions.Tools!);
            Assert.Contains(repositoryTool, requestOptions.Tools!);
        });
        Assert.Contains(provider.Requests[1], message => message.Text == prefetch.Text);
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
            [new ChatMessage(ChatRole.System, "Analyze C# source and use dotnet when appropriate."),
             new ChatMessage(ChatRole.User, "Change the timeout styling in styles.css.")], options);
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent("search-1", "Services/TimeoutService.cs:42"),
            ])], options);

        Assert.Equal(1, graphCalls);
        Assert.Single(provider.Options[0].Tools!);
        Assert.Single(provider.Options[1].Tools!);
    }

    [Theory]
    [InlineData("No symbol found.", false)]
    [InlineData("[]", false)]
    [InlineData("", true)]
    public async Task UnproductivePrefetchRetainsAllGraphTools(string result, bool fails)
    {
        int graphCalls = 0;
        AIFunction getContext = AIFunctionFactory.Create(
            (Func<string, int, int, string>)((query, maxNodes, tokenBudget) =>
            {
                graphCalls++;
                if (fails) throw new InvalidOperationException("Graph lookup failed");
                return result;
            }), name: CodeGraphToolFactory.GetContextName);
        AIFunction[] graphTools =
        [
            getContext,
            AIFunctionFactory.Create((string name) => "symbol-id", name: CodeGraphToolFactory.FindSymbolName),
            AIFunctionFactory.Create((string id) => "neighbors", name: CodeGraphToolFactory.GetNeighborsName),
            AIFunctionFactory.Create((string id) => "context", name: CodeGraphToolFactory.GetContextByIdName),
        ];
        RecordingChatClient provider = new();
        CodeGraphPrefetchChatClient client = new(provider);
        ChatOptions options = new() { Tools = [.. graphTools] };
        ChatMessage[] messages = [new(ChatRole.User, "Inspect OrderService.cs")];

        await client.GetResponseAsync(messages, options);
        await client.GetResponseAsync(messages, options);

        Assert.Equal(1, graphCalls);
        Assert.All(provider.Options, request => Assert.Equal(graphTools, request.Tools!.OfType<AIFunction>()));
        Assert.All(provider.Requests, request => Assert.Contains(request,
            message => message.Text?.Contains(fails ? "prefetch for symbol" : "context prefetched locally",
                StringComparison.Ordinal) is true));
    }

    [Fact]
    public async Task ExistingGraphCallSkipsPrefetchAndRetainsToolsForFollowup()
    {
        AIFunction getContext = AIFunctionFactory.Create(
            (Func<string, string>)(_ => throw new InvalidOperationException("Prefetch must not run")),
            name: CodeGraphToolFactory.GetContextName);
        RecordingChatClient provider = new();
        CodeGraphPrefetchChatClient client = new(provider);
        ChatOptions options = new() { Tools = [getContext] };
        ChatMessage[] messages =
        [
            new(ChatRole.User, "Inspect OrderService.cs"),
            new(ChatRole.Assistant,
                [new FunctionCallContent("lookup", CodeGraphToolFactory.FindSymbolName,
                    new Dictionary<string, object?> { ["name"] = "OrderService" })]),
        ];

        await client.GetResponseAsync(messages, options);

        Assert.Equal(messages, provider.Requests[0]);
        Assert.Contains(getContext, provider.Options[0].Tools!);
    }

    [Fact]
    public async Task StreamingRequestsRetainGraphContextAndTools()
    {
        int graphCalls = 0;
        AIFunction getContext = AIFunctionFactory.Create(
            (string query, int maxNodes, int tokenBudget) =>
            {
                graphCalls++;
                return "OrderService graph context";
            }, name: CodeGraphToolFactory.GetContextName);
        RecordingChatClient provider = new();
        CodeGraphPrefetchChatClient client = new(provider);
        ChatOptions options = new() { Tools = [getContext] };

        for (int request = 0; request < 2; request++)
            await foreach (var update in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "Inspect OrderService.cs")], options))
                Assert.NotNull(update);

        Assert.Equal(1, graphCalls);
        Assert.Equal(2, provider.CallCount);
        Assert.All(provider.Options, request => Assert.Contains(getContext, request.Tools!));
        Assert.All(provider.Requests, request => Assert.Contains(request,
            message => message.Text?.Contains("OrderService graph context", StringComparison.Ordinal) is true));
    }

    [Fact]
    public async Task PropagatesPrefetchCancellationWithoutCallingProvider()
    {
        using CancellationTokenSource cancellation = new();
        AIFunction getContext = AIFunctionFactory.Create(
            (string query, int maxNodes, int tokenBudget) =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
                return "unreachable";
            }, name: CodeGraphToolFactory.GetContextName);
        RecordingChatClient provider = new();
        CodeGraphPrefetchChatClient client = new(provider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Inspect OrderService.cs")],
            new ChatOptions { Tools = [getContext] }, cancellation.Token));

        Assert.Equal(0, provider.CallCount);
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

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
