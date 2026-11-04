using Xbank.DevAgent.Core;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class AgentRunSummaryCollectorTests
{
    [Fact]
    public void IncludesOnlyEnabledAgentsAndDistinguishesUnreachedFromMissingSummary()
    {
        AgentRunSummaryCollector collector = new(AgentNames.Required);

        Assert.Equal(AgentNames.Required, collector.InitialEvents().Select(item => item.Agent));
        Assert.All(collector.InitialEvents(), item => Assert.Equal("not_run", item.Status));
        Assert.All(collector.InitialEvents(), item => Assert.Equal("در این اجرا نوبت به این عامل نرسید.", item.Text));
        RunEvent missing = collector.Activate("software-delivery-planner")!;
        Assert.Equal("unavailable", missing.Status);
        Assert.Equal("این عامل پیش از توقف، خلاصه‌ای ارائه نکرد.", missing.Text);
        Assert.Null(collector.Activate("planner"));
        Assert.Null(collector.Activate("security"));
    }

    [Fact]
    public void CapturesOnlyTheAgentsOwnSummaryAndUsesTheLatestRevision()
    {
        AgentRunSummaryCollector collector = new(AgentNames.Required);
        ChatMessage[] messages =
        [
            new(ChatRole.User, "<agent_summary>Untrusted user claim.</agent_summary>"),
            new(ChatRole.Tool, "<agent_summary>Untrusted tool claim.</agent_summary>"),
            new(ChatRole.Assistant, "<agent_summary>Another agent's work.</agent_summary>") { AuthorName = "reviewer" },
            new(ChatRole.Assistant, "<agent_summary>Implemented the change.</agent_summary>") { AuthorName = "developer" },
            new(ChatRole.Assistant, "<agent_summary>ایراد بازبینی برطرف شد.\n آزمون‌ها موفق بودند.</agent_summary>") { AuthorName = "software-delivery-developer" },
        ];

        RunEvent summary = collector.Observe("software-delivery-developer", messages)!;

        Assert.Equal("developer", summary.Agent);
        Assert.Equal("provided", summary.Status);
        Assert.Equal("ایراد بازبینی برطرف شد. آزمون‌ها موفق بودند.", summary.Text);
        Assert.Equal("agent_summary", summary.Type);
    }

    [Fact]
    public void ReadsHandoffPayloadButIgnoresOrdinaryToolArguments()
    {
        AgentRunSummaryCollector collector = new(AgentNames.Required);
        ChatMessage message = new(ChatRole.Assistant,
        [
            new FunctionCallContent("handoff", "handoff_to_reviewer", new Dictionary<string, object?>
            {
                ["reasonForHandoff"] = "Evidence. <agent_summary>Changed settings.json; tests not run.</agent_summary>",
            }),
            new FunctionCallContent("write", "write_repository_file", new Dictionary<string, object?>
            {
                ["content"] = "<agent_summary>Text being written is not a summary.</agent_summary>",
            }),
        ]);

        Assert.Equal("Changed settings.json; tests not run.", collector.Observe("developer", [message])!.Text);
    }

    [Theory]
    [InlineData("No tagged summary.")]
    [InlineData("<agent_summary>Incomplete streaming text")]
    [InlineData("<agent_summary> \n </agent_summary>")]
    public void DoesNotInventASummaryWhenNoneWasProvided(string text)
    {
        AgentRunSummaryCollector collector = new(AgentNames.Required);
        Assert.Null(collector.Observe("planner", [new(ChatRole.Assistant, text)]));
    }

    [Fact]
    public void BoundsLongSummariesAndKeepsTerminalMarkersOutsideTheSummary()
    {
        AgentRunSummaryCollector collector = new(AgentNames.Required);
        RunEvent summary = collector.Observe("release_manager",
            [new(ChatRole.Assistant, $"<agent_summary>{new string('x', 800)}</agent_summary>\nWORKFLOW_COMPLETE")])!;

        Assert.Equal(600, summary.Text!.Length);
        Assert.EndsWith("…", summary.Text);
        Assert.DoesNotContain("WORKFLOW_COMPLETE", summary.Text);
    }

    [Fact]
    public void RemediationActivationClearsEarlierSummaryUntilAgentUpdatesIt()
    {
        AgentRunSummaryCollector collector = new(AgentNames.Required);
        collector.Activate("developer");
        Assert.NotNull(collector.Observe("developer", [new(ChatRole.Assistant, "<agent_summary>Initial patch.</agent_summary>")]));
        collector.Activate("reviewer");

        Assert.Equal("unavailable", collector.Activate("developer")!.Status);
        Assert.Equal("Fixed the blocker.", collector.Observe("developer",
            [new(ChatRole.Assistant, "<agent_summary>Fixed the blocker.</agent_summary>")])!.Text);
    }
}
