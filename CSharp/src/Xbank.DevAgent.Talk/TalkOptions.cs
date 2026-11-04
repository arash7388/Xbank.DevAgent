namespace Xbank.DevAgent.Talk;

public sealed class TalkOptions
{
    public const string SectionName = "Talk";
    public bool Enabled { get; set; }
    public bool NotifyAgentSteps { get; set; } = true;
    public bool IncludeDetails { get; set; }
    public long ThreadId { get; set; }
    public string Token { get; set; } = "";
    public string QueueHost { get; set; } = "";
    public int QueuePort { get; set; } = 61616;
    public string QueueUsername { get; set; } = "";
    public string QueuePassword { get; set; } = "";
    public string QueueReceive { get; set; } = "";
    public string QueueSend { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string FileServer { get; set; } = "";
    public string PodspaceHost { get; set; } = "";
    public string PlatformHost { get; set; } = "";
    public string SsoHost { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 15;

    public bool IsValid() => !Enabled ||
        (ThreadId > 0 && QueuePort is > 0 and <= 65535 && TimeoutSeconds is >= 1 and <= 120 &&
         new[] { Token, QueueHost, QueueUsername, QueuePassword, QueueReceive, QueueSend, ServerName }
             .All(value => !string.IsNullOrWhiteSpace(value)) &&
         new[] { FileServer, PodspaceHost, PlatformHost, SsoHost }
             .All(value => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https"));
}
