namespace Xbank.DevAgent.Core;

public sealed record AifaRequestContext(string UserId, string SessionId, string? RequestId = null)
{
    private const string DefaultEndpoint = "https://aifa-chatbot.dev.dotin.ir";

    /// <summary>Endpoint supplied by application configuration when the environment variable is absent.</summary>
    public static string? ConfiguredEndpoint { get; private set; }

    public static string Endpoint => ResolveEndpoint(Environment.GetEnvironmentVariable("AIFA_ENDPOINT"), ConfiguredEndpoint);

    public static void ConfigureEndpoint(string? endpoint) =>
        ConfiguredEndpoint = string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim();

    // AIFA_ENDPOINT keeps precedence so a process-level override always wins over stored settings.
    public static string ResolveEndpoint(string? environmentEndpoint, string? configuredEndpoint) =>
        environmentEndpoint?.Trim() is { Length: > 0 } fromEnvironment ? fromEnvironment
        : configuredEndpoint?.Trim() is { Length: > 0 } fromConfiguration ? fromConfiguration
        : DefaultEndpoint;

    public const string Model = "developer-model";

    public static bool IsAifa(Uri endpoint) => Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? configured)
        && endpoint.Scheme.Equals(configured.Scheme, StringComparison.OrdinalIgnoreCase)
        && endpoint.Authority.Equals(configured.Authority, StringComparison.OrdinalIgnoreCase);

    public void Validate()
    {
        ValidateHeader(UserId, "x-user-id");
        ValidateHeader(SessionId, "x-session-id");
        if (RequestId is not null) ValidateHeader(RequestId, "x-request-id");
    }

    private static void ValidateHeader(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ConfigurationException($"AIFA {name} must be a nonempty header value of at most 256 characters without control characters.");
    }
}

internal sealed class AifaRequestHandler(AifaRequestContext context, HttpMessageHandler innerHandler)
    : DelegatingHandler(innerHandler)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not null && AifaRequestContext.IsAifa(request.RequestUri))
        {
            // Request identity survives retries; agents share a workflow session identity.
            request.Headers.Add("x-request-id", context.RequestId ?? Guid.NewGuid().ToString("N"));
            request.Headers.Add("x-session-id", context.SessionId);
            request.Headers.Add("x-user-id", context.UserId);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
