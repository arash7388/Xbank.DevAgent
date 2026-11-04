using System.Runtime.CompilerServices;
using Xbank.DevAgent.Core;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class ToolRoundBudgetChatClientTests
{
    [Fact]
    public async Task RequiresSoleHandoffAfterOrdinaryToolRoundBudgetIsReached()
    {
        RecordingChatClient provider = new(_ => ToolCall("search_repository"));
        ToolRoundBudgetChatClient client = new(provider, maximumOrdinaryToolRounds: 3);
        ChatOptions options = CreateOptions();

        for (int index = 0; index < 4; index++)
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Plan the change.")], options);
        }

        Assert.Equal(2, provider.Options[2].Tools!.Count);
        ChatOptions constrained = provider.Options[3];
        AITool handoff = Assert.Single(constrained.Tools!);
        Assert.Equal("handoff_to_developer", handoff.Name);
        Assert.Null(constrained.ToolMode);
        Assert.Contains("TOOL ROUND BUDGET REACHED", constrained.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertsFinalPlannerTextToHandoffWithoutToolChoice()
    {
        int call = 0;
        RecordingChatClient provider = new(_ => call++ == 0
            ? ToolCall("search_repository")
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, "Change Settings.cs and preserve its format.")));
        ToolRoundBudgetChatClient client = new(provider, maximumOrdinaryToolRounds: 1);
        ChatOptions options = CreateOptions();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Plan the change.")], options);
        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Plan the change.")], options);

        FunctionCallContent handoff = Assert.Single(
            response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal("handoff_to_developer", handoff.Name);
        IDictionary<string, object?> arguments = Assert.IsAssignableFrom<IDictionary<string, object?>>(handoff.Arguments);
        Assert.Contains("Settings.cs", arguments["reasonForHandoff"]?.ToString(), StringComparison.Ordinal);
        Assert.Null(provider.Options[1].ToolMode);
    }

    [Fact]
    public async Task HandoffOnlyResponsesDoNotConsumeOrdinaryToolBudget()
    {
        RecordingChatClient provider = new(_ => ToolCall("handoff_to_developer"));
        ToolRoundBudgetChatClient client = new(provider, maximumOrdinaryToolRounds: 1);
        ChatOptions options = CreateOptions();

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Plan the change.")], options);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Plan the change.")], options);

        Assert.All(provider.Options, sent => Assert.Equal(2, sent.Tools!.Count));
    }

    private static ChatOptions CreateOptions() => new()
    {
        Instructions = "Inspect the repository and hand off a plan.",
        Tools =
        [
            AIFunctionFactory.Create((Func<string, string>)(query => query), name: "search_repository"),
            AIFunctionFactory.Create((Func<string>)(() => "handoff"), name: "handoff_to_developer"),
        ],
    };

    private static ChatResponse ToolCall(string name) => new(new ChatMessage(
        ChatRole.Assistant,
        [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, new Dictionary<string, object?>())]));

    private sealed class RecordingChatClient(Func<ChatOptions?, ChatResponse> response) : IChatClient
    {
        public List<ChatOptions> Options { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Options.Add(options ?? new ChatOptions());
            return Task.FromResult(response(options));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Options.Add(options ?? new ChatOptions());
            await Task.Yield();
            foreach (ChatResponseUpdate update in response(options).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
