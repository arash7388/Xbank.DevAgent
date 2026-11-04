using Xbank.DevAgent.Core;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class CompletedToolCycleFilteringChatClientTests
{
    [Fact]
    public void ProgressTextDoesNotDiscardEvidenceNeededForReview()
    {
        ChatMessage[] history =
        [
            ToolCall("diff", "get_repository_diff"),
            ToolResult("diff", "The requested fix is present."),
            new(ChatRole.Assistant, "Now checking the tests."),
            ToolCall("tests", "read_repository_file"),
            ToolResult("tests", "Test contents"),
        ];

        Assert.Equal(history, CompletedToolCycleFilteringChatClient.Filter(history));
    }

    [Fact]
    public void RemovesOrdinaryToolCycleAfterHandoff()
    {
        ChatMessage call = ToolCall("call-1", "read_repository_file");
        ChatMessage result = ToolResult("call-1", "large file contents");
        ChatMessage response = ToolCall("handoff", "handoff_to_1");

        IReadOnlyList<ChatMessage> filtered = CompletedToolCycleFilteringChatClient.Filter(
            [new ChatMessage(ChatRole.User, "Inspect the file."), call, result, response]);

        Assert.Equal(2, filtered.Count);
        Assert.Equal(ChatRole.User, filtered[0].Role);
        Assert.Same(response, filtered[1]);
        Assert.Single(call.Contents);
        Assert.Single(result.Contents);
    }

    [Fact]
    public void KeepsEarlierEvidenceWhileTheAgentRequestsMoreTools()
    {
        ChatMessage oldCall = ToolCall("call-1", "search_repository");
        ChatMessage oldResult = ToolResult("call-1", "search matches");
        ChatMessage nextCall = ToolCall("call-2", "read_repository_file");
        ChatMessage nextResult = ToolResult("call-2", "file contents");

        IReadOnlyList<ChatMessage> filtered = CompletedToolCycleFilteringChatClient.Filter(
            [new ChatMessage(ChatRole.User, "Make the change."), oldCall, oldResult, nextCall, nextResult]);

        Assert.Equal(5, filtered.Count);
        Assert.Same(oldResult, filtered[2]);
        Assert.Same(nextCall, filtered[3]);
        Assert.Same(nextResult, filtered[4]);
    }

    [Fact]
    public void PreservesHandoffCallsAndTheirResults()
    {
        ChatMessage handoff = ToolCall("handoff-1", "handoff_to_1");
        ChatMessage result = ToolResult("handoff-1", "Transferred.");
        ChatMessage response = new(ChatRole.Assistant, "Continue.");

        IReadOnlyList<ChatMessage> filtered = CompletedToolCycleFilteringChatClient.Filter(
            [handoff, result, response]);

        Assert.Equal(3, filtered.Count);
        Assert.Same(handoff, filtered[0]);
        Assert.Same(result, filtered[1]);
    }

    [Fact]
    public void RetainsNonToolContentWhenRemovingAConsumedCall()
    {
        ChatMessage mixed = new(ChatRole.Assistant,
        [
            new TextContent("Inspecting now."),
            new FunctionCallContent("call-1", "read_repository_file", new Dictionary<string, object?>()),
        ]);
        ChatMessage result = ToolResult("call-1", "contents");
        ChatMessage response = ToolCall("handoff", "handoff_to_1");

        IReadOnlyList<ChatMessage> filtered = CompletedToolCycleFilteringChatClient.Filter(
            [mixed, result, response]);

        Assert.Equal(2, filtered.Count);
        Assert.Equal("Inspecting now.", filtered[0].Text);
        Assert.Empty(filtered[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal(2, mixed.Contents.Count);
    }

    private static ChatMessage ToolCall(string callId, string name) => new(
        ChatRole.Assistant,
        [new FunctionCallContent(callId, name, new Dictionary<string, object?>())]);

    private static ChatMessage ToolResult(string callId, string result) => new(
        ChatRole.Tool,
        [new FunctionResultContent(callId, result)]);
}
