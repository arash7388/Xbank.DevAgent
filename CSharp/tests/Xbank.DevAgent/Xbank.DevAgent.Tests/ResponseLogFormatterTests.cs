using System.Text.Json;
using Xbank.DevAgent.Core;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class ResponseLogFormatterTests
{
    [Fact]
    public void ShowsReplyToolResultsAndHandoffReason()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.Assistant,
            [
                new TextReasoningContent("Internal reasoning"),
                new FunctionCallContent("search-1", "search_documents", new Dictionary<string, object?>()),
                new FunctionCallContent("search-2", "search_documents", new Dictionary<string, object?>()),
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("search-1", "First result")]),
            new(ChatRole.Tool, [new FunctionResultContent("search-2", "Found the requirements document.")]),
            new(ChatRole.Assistant,
            [
                new TextContent("<agent_summary>Plan ready.\n Update the configuration.</agent_summary>"),
                new FunctionCallContent("handoff-1", "handoff_to_developer", new Dictionary<string, object?>
                {
                    ["reasonForHandoff"] = JsonSerializer.SerializeToElement("Implement the configuration change."),
                }),
            ]),
        ];

        string log = ResponseLogFormatter.Format(messages);

        Assert.Contains("Reply: Plan ready. Update the configuration.", log);
        Assert.Contains("Tool search_documents: 2 requested, 2 results returned; latest result: Found the requirements document.", log);
        Assert.Contains("Handoff requested to developer: Implement the configuration change.", log);
        Assert.DoesNotContain("Internal reasoning", log);
        Assert.DoesNotContain("<agent_summary>", log);
        Assert.DoesNotContain("Finish reason", log);
    }

    [Fact]
    public void UsesLastAssistantReplyAndBoundsLongPreviews()
    {
        string log = ResponseLogFormatter.Format(
        [
            new(ChatRole.Assistant, "Earlier reply"),
            new(ChatRole.Assistant, new string('x', 399) + "😀 more text"),
            new(ChatRole.Tool, "Tool text is not an assistant reply"),
        ], "length");

        Assert.Contains("Reply: " + new string('x', 399) + "…", log);
        Assert.Contains("Finish reason: length.", log);
        Assert.DoesNotContain("Earlier reply", log);
        Assert.DoesNotContain("Tool text", log);
        Assert.DoesNotContain("more text", log);
    }

    [Fact]
    public void DescribesEmptyResponsesWithoutInventingAnOutcome()
    {
        string log = ResponseLogFormatter.Format([new(ChatRole.Assistant, [new TextReasoningContent("Reasoning")])]);

        Assert.Equal("Response completed. No assistant text or tool calls were returned.", log);
    }
}
