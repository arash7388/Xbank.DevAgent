using System.Collections.Concurrent;
using System.Reflection;
using Maf.Handoff.Api;
using Maf.Handoff.Core;
using Maf.Handoff.Talk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Maf.Handoff.Tests;

public sealed class TalkNotificationTests
{
    [Fact]
    public void EventsSelectFinishedStepsAndHumanBlockers()
    {
        Guid runId = Guid.NewGuid();
        Assert.Equal(new(runId, "developer", "step_finished", "Handed off to reviewer."),
            TalkNotification.FromEvent(runId, RunEvent.Handoff("developer", "reviewer")));
        Assert.Null(TalkNotification.FromEvent(runId, RunEvent.Handoff("developer", "reviewer"), false));
        Assert.Equal("awaiting_input", TalkNotification.FromEvent(runId,
            new("clarification_requested", Text: "Which target?"))!.Status);
        Assert.Null(TalkNotification.FromEvent(runId, RunEvent.Delta("developer", "token")));
        Assert.Null(TalkNotification.FromEvent(runId, RunEvent.AgentSummary("reviewer", "pending", "not_run")));
        // Repository session status supplies the final outcome after publishing operations.
        Assert.Null(TalkNotification.FromEvent(runId, RunEvent.Finished("completed")));
    }

    [Fact]
    public void DetailsRequireOptInAndAreBounded()
    {
        TalkNotification notification = new(Guid.NewGuid(), "workflow", "failed", "private detail");
        Assert.DoesNotContain("private detail", notification.Format(false));
        Assert.Contains("private detail", notification.Format(true));
        Assert.True((notification with { Details = new string('a', 5000) }).Format(true).Length < 2100);
    }

    [Fact]
    public void DisabledRegistrationNeedsNoCredentialsAndEnabledSettingsAreValidated()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddTalkNotifications(new ConfigurationBuilder().Build());
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<IOptions<TalkOptions>>().Value.Enabled);
        Assert.NotNull(provider.GetRequiredService<ITalkNotifications>());
        Assert.False(new TalkOptions { Enabled = true }.IsValid());
    }

    [Fact]
    public async Task DeliveryFailureDoesNotStopLaterNotificationsAndShutdownDrainsQueue()
    {
        RecordingClient client = new() { FailFirst = true };
        using TalkNotificationService service = new(client, Options.Create(new TalkOptions { Enabled = true, ThreadId = 42 }),
            NullLogger<TalkNotificationService>.Instance);
        await service.StartAsync(CancellationToken.None);
        service.Publish(new(Guid.NewGuid(), "workflow", "failed"));
        service.Publish(new(Guid.NewGuid(), "workflow", "completed"));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await service.StopAsync(timeout.Token);
        Assert.Equal(2, client.Messages.Count);
        Assert.All(client.Messages, item => Assert.Equal(42, item.ThreadId));
    }

    [Fact]
    public async Task DisabledNotificationsAndStepPreferenceDoNotSend()
    {
        RecordingClient client = new();
        TalkOptions options = new();
        using TalkNotificationService service = new(client, Options.Create(options), NullLogger<TalkNotificationService>.Instance);
        service.Publish(new(Guid.NewGuid(), "workflow", "completed"));
        options.Enabled = true;
        options.NotifyAgentSteps = false;
        service.Publish(new(Guid.NewGuid(), "developer", "step_finished"));
        await service.StartAsync(CancellationToken.None);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await service.StopAsync(timeout.Token);
        Assert.Empty(client.Messages);
    }

    [Fact]
    public void RepeatedSessionPersistenceDoesNotDuplicateOutcomeNotifications()
    {
        Guid id = Guid.NewGuid();
        CreateSessionRequest request = new("https://example.test/repo.git", "main", "Task 12345", new Dictionary<string, AgentModelConfiguration>(), null);
        Type type = typeof(RepositorySessionService).GetNestedType("SessionState", BindingFlags.NonPublic)!;
        object state = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [id, request, (Action<RunEvent>)(_ => { })], null)!;
        type.GetMethod("MarkFailed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, ["Build prerequisite missing", null]);
        SessionSnapshot snapshot = (SessionSnapshot)type.GetMethod("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, null)!;
        RecordingNotifications notifications = new();
        MethodInfo notify = type.GetMethod("NotifyStatusChange", BindingFlags.Instance | BindingFlags.NonPublic)!;
        notify.Invoke(state, [snapshot, notifications]);
        notify.Invoke(state, [snapshot, notifications]);
        Assert.Equal("failed", Assert.Single(notifications.Items).Status);
        Assert.Equal("Build prerequisite missing", notifications.Items[0].Details);
        SessionSnapshot pushed = snapshot with { Status = "pushed", Error = null };
        notify.Invoke(state, [pushed, notifications]);
        notify.Invoke(state, [pushed with { Error = "PR creation failed" }, notifications]);
        Assert.Equal("pull_request", notifications.Items[^1].Step);
        Assert.Equal("failed", notifications.Items[^1].Status);
    }

    private sealed class RecordingNotifications : ITalkNotifications
    {
        public List<TalkNotification> Items { get; } = [];
        public void Publish(TalkNotification notification) => Items.Add(notification);
    }

    private sealed class RecordingClient : ITalkMessageClient
    {
        public bool FailFirst { get; init; }
        public ConcurrentQueue<(long ThreadId, string Text)> Messages { get; } = new();
        public Task SendAsync(long threadId, string text, CancellationToken cancellationToken)
        {
            Messages.Enqueue((threadId, text));
            if (FailFirst && Messages.Count == 1) throw new InvalidOperationException("Simulated Talk outage");
            return Task.CompletedTask;
        }
    }
}
