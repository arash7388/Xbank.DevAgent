using System.Runtime.CompilerServices;
using Xbank.DevAgent.Core;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class JiraScopeChangeReportingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RecordsDeclaredExtraChangesBeforeHandoffEvenWithoutReportingTool(bool streaming, bool inPayload)
    {
        string changes = "اصلاح تکمیلی در Validator.cs برای رفع خطای بازبینی؛ آزمون موفق بود.";
        string block = $"<jira_scope_change>{changes}</jira_scope_change>";
        ChatMessage message = new(ChatRole.Assistant, [new FunctionCallContent("handoff", "handoff_to_reviewer",
            new Dictionary<string, object?> { ["reason"] = inPayload ? block : "Implementation verified." })]);
        if (!inPayload) message.Contents.Add(new TextContent(block));
        Assert.False(CheckpointSerialization.SerializeResponse(new(message)).Reusable);
        Reporter reporter = new();
        using JiraScopeChangeReportingChatClient guard = new(new Client(new(message)), reporter);
        ChatResponse response;
        if (streaming)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in guard.GetStreamingResponseAsync([])) updates.Add(update);
            response = updates.ToChatResponse();
        }
        else response = await guard.GetResponseAsync([]);
        Assert.Equal(changes, reporter.Changes);
        Assert.Equal(1, reporter.Calls);
        Assert.Contains("posted", response.Text);
        Assert.Equal("handoff_to_reviewer", Assert.Single(response.Messages.SelectMany(item => item.Contents).OfType<FunctionCallContent>()).Name);
    }

    [Theory]
    [InlineData("report_jira_scope_change")]
    [InlineData("ask_jira_clarification")]
    public void InteractiveAndCommentCallsAreNeverReusableCheckpoints(string tool)
    {
        ChatResponse response = new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call", tool, new Dictionary<string, object?>())]));
        Assert.False(CheckpointSerialization.SerializeResponse(response).Reusable);
    }

    [Fact]
    public async Task ScopedImplementationDoesNotPostRoutineComment()
    {
        Reporter reporter = new();
        using JiraScopeChangeReportingChatClient guard = new(new Client(new(new ChatMessage(ChatRole.Assistant, "Implemented the Jira requirements."))), reporter);
        await guard.GetResponseAsync([]);
        Assert.Equal(0, reporter.Calls);
    }

    private sealed class Reporter : IJiraScopeChangeReporter
    {
        public int Calls { get; private set; }
        public string? Changes { get; private set; }
        public Task<JiraProblemReport> ReportScopeChangeAsync(string changesInPersian, CancellationToken cancellationToken = default)
        {
            Calls++;
            Changes = changesInPersian;
            return Task.FromResult(new JiraProblemReport("TASK-42", "posted", changesInPersian));
        }
    }

    private sealed class Client(ChatResponse response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(response);
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
