using System.Text.Json;
using Maf.Handoff.Api;
using Maf.Handoff.Core;

namespace Maf.Handoff.Tests;

public sealed class SessionFailureDetailsTests
{
    [Fact]
    public void LoopStopShowsTheGuardExplanationAndHandoffPath()
    {
        string message = HandoffTopology.LoopDetectedMarker + " Recent path: developer -> reviewer -> developer.";
        SessionFailureDetails details = SessionFailureDetails.FromEvents("Stopped without progress",
            [RunEvent.Delta("developer", message), RunEvent.Finished("loop_detected")])!;
        Assert.Equal("Workflow loop detected", details.Title);
        Assert.Equal(message, details.Message);
        Assert.Equal("developer", details.Agent);
    }
    [Fact]
    public void ReassemblesArchivedStreamingAssessmentWithoutOtherAgentsOrToolRounds()
    {
        string explanation = "## Blocking contradiction\nRequest and issue differ.\n\n**Questions**\n1. Which scope is authoritative?\nJIRA_INPUT_REQUIRED";
        List<RunEvent> events =
        [
            RunEvent.Delta("planner", "Unrelated earlier output"),
            RunEvent.Tool("jira", "Read issue"),
            RunEvent.Delta("jira", "Inspecting repository"),
            RunEvent.Tool("jira", "Read file"),
            .. explanation.Select(character => RunEvent.Delta("jira", character.ToString())),
            RunEvent.Trace("jira", "Response complete", "response"),
            RunEvent.Finished("input_required"),
        ];

        SessionFailureDetails details = SessionFailureDetails.FromEvents("Additional input required", events)!;

        Assert.Equal("Clarification needed", details.Title);
        Assert.Equal("jira", details.Agent);
        Assert.Equal("input_required", details.Status);
        Assert.Equal(explanation, details.Message);
        Assert.DoesNotContain("Inspecting repository", details.Message);
        Assert.DoesNotContain("Unrelated earlier output", details.Message);
    }

    [Fact]
    public void ExistingSnapshotGetsFullDetailsWithoutPersistedDiagnosticField()
    {
        string fullMessage = new string('x', 25000) + "\nJIRA_INPUT_REQUIRED";
        SessionSnapshot snapshot = new(Guid.NewGuid(), "https://github.com/example/repo", "github", "main",
            "WEPOD-42", DateTimeOffset.UtcNow, "failed",
            [RunEvent.Delta("jira", fullMessage), RunEvent.Finished("input_required")], "", "Input required", null, null, null);
        // Emulate snapshots saved by versions that predate FailureDetails.
        var old = JsonSerializer.SerializeToNode(snapshot)!.AsObject();
        old.Remove("FailureDetails");
        SessionSnapshot restored = JsonSerializer.Deserialize<SessionSnapshot>(old.ToJsonString())!;

        Assert.Equal(fullMessage, restored.FailureDetails!.Message);
        Assert.Contains(fullMessage.Replace("\n", "\\n"), JsonSerializer.Serialize(restored));
    }

    [Fact]
    public void ReleaseRejectionUsesReleaseExplanationRatherThanReviewOutput()
    {
        SessionFailureDetails details = SessionFailureDetails.FromEvents("Rejected before release",
        [
            RunEvent.Delta("reviewer", "REVIEW_APPROVED"),
            RunEvent.Delta("software-delivery-release_manager", "Missing acceptance check. WORKFLOW_REJECTED"),
            RunEvent.Finished("rejected_by_release"),
        ])!;

        Assert.Equal("Release blocked", details.Title);
        Assert.Equal("Missing acceptance check. WORKFLOW_REJECTED", details.Message);
    }

    [Fact]
    public void InfrastructureFailureDoesNotPresentAnEarlierAgentResponseAsItsCause()
    {
        SessionFailureDetails details = SessionFailureDetails.FromEvents("Git clone failed: branch missing",
            [RunEvent.Delta("jira", "Earlier assessment")])!;

        Assert.Null(details.Agent);
        Assert.Equal("Git clone failed: branch missing", details.Message);
        Assert.Null(SessionFailureDetails.FromEvents(null, []));
    }
}
