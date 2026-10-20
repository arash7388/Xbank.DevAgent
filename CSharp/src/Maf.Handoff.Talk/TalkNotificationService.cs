using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maf.Handoff.Talk;

public sealed class TalkNotificationService(
    ITalkMessageClient client,
    IOptions<TalkOptions> options,
    ILogger<TalkNotificationService> logger) : BackgroundService, ITalkNotifications
{
    private readonly Channel<TalkNotification> _queue = Channel.CreateBounded<TalkNotification>(
        new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public void Publish(TalkNotification notification)
    {
        if (notification.Status == "step_finished" && !options.Value.NotifyAgentSteps) return;
        if (options.Value.Enabled && !_queue.Writer.TryWrite(notification))
            logger.LogWarning("Talk notification queue is full or stopping. Notification for run {RunId} was dropped.", notification.RunId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (TalkNotification notification in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds * 2));
                    await client.SendAsync(options.Value.ThreadId, notification.Format(options.Value.IncludeDetails), timeout.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    // SDK exceptions may contain credentials or message payloads.
                    logger.LogWarning("Talk notification delivery failed for run {RunId}, step {Step}, status {Status}.",
                        notification.RunId, notification.Step, notification.Status);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        try
        {
            if (ExecuteTask is not null) await ExecuteTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { await base.StopAsync(cancellationToken); }
    }
}
