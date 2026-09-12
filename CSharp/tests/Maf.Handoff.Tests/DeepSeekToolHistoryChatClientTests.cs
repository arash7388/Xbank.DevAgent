using Maf.Handoff.Core;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class DeepSeekToolHistoryChatClientTests
{
    [Fact]
    public void KeepsToolResultWhenImmediatelyPrecededByMatchingCall()
    {
        ChatMessage call = new(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>())]);
        ChatMessage result = new(ChatRole.Tool, [new FunctionResultContent("call-1", "contents")]);

        IReadOnlyList<ChatMessage> normalized = DeepSeekToolHistoryChatClient.Normalize([call, result]);

        Assert.Equal(2, normalized.Count);
        Assert.Equal(ChatRole.Tool, normalized[1].Role);
    }

    [Fact]
    public void ConvertsOrphanedToolResultToUserContext()
    {
        ChatMessage result = new(ChatRole.Tool, [new FunctionResultContent("call-1", "contents")]);

        ChatMessage normalized = Assert.Single(DeepSeekToolHistoryChatClient.Normalize([result]));

        Assert.Equal(ChatRole.User, normalized.Role);
        Assert.Contains("call-1", normalized.Text, StringComparison.Ordinal);
        Assert.Contains("contents", normalized.Text, StringComparison.Ordinal);
        Assert.Empty(normalized.Contents.OfType<FunctionResultContent>());
    }

    [Fact]
    public void ConvertsToolResultWhenAnInterveningMessageBreaksAdjacency()
    {
        ChatMessage call = new(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>())]);
        ChatMessage intervening = new(ChatRole.User, "Continue in the next agent.");
        ChatMessage result = new(ChatRole.Tool, [new FunctionResultContent("call-1", "contents")]);

        IReadOnlyList<ChatMessage> normalized = DeepSeekToolHistoryChatClient.Normalize([call, intervening, result]);

        Assert.Equal(ChatRole.User, normalized[2].Role);
    }
}
