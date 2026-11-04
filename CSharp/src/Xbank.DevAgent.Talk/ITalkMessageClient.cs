namespace Xbank.DevAgent.Talk;

public interface ITalkMessageClient
{
    Task SendAsync(long threadId, string text, CancellationToken cancellationToken);
}

public interface ITalkNotifications
{
    void Publish(TalkNotification notification);
}
