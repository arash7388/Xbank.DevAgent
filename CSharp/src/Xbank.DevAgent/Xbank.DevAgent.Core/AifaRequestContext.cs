namespace Xbank.DevAgent.Core;

public sealed record AifaRequestContext(string UserId, string SessionId, string? RequestId = null)
{
    public static string Endpoint => Environment.GetEnvironmentVariable("AIFA_ENDPOINT")?.Trim() is { Length: > 0 } endpoint
        ? endpoint : "https://aifa.example.com/v1";
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
