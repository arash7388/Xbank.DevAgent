using System.Text.Json;

namespace Xbank.DevAgent.Core;

public sealed record AifaRequestContext(string UserId, string SessionId, string? RequestId = null)
{
    private static readonly Lazy<string> BundledEndpoint = new(() =>
        ReadEndpoint(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));

    /// <summary>Shared endpoint loaded from appsettings.json by the application host.</summary>
    public static string? ConfiguredEndpoint { get; private set; }

    public static string Endpoint => ConfiguredEndpoint ?? BundledEndpoint.Value;

    public static void ConfigureEndpointFromFile(string path) => ConfiguredEndpoint = ReadEndpoint(path);

    internal static string ReadEndpoint(string path)
    {
        using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        if (!settings.RootElement.TryGetProperty("Providers", out JsonElement providers)
            || !providers.TryGetProperty("AIFA", out JsonElement aifa)
            || !aifa.TryGetProperty("Endpoint", out JsonElement endpoint)
            || endpoint.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(endpoint.GetString()))
            throw new ConfigurationException("Providers:AIFA:Endpoint is required in appsettings.json.");
        return ModelProviderDefinition.NormalizeEndpoint(endpoint.GetString()!);
    }

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

internal sealed class AifaRequestHandler(AifaRequestContext context, HttpMessageHandler innerHandler,
    Action<string>? logRequest = null)
    : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not null && AifaRequestContext.IsAifa(request.RequestUri))
        {
            // Request identity survives retries; agents share a workflow session identity.
            request.Headers.Add("x-request-id", context.RequestId ?? Guid.NewGuid().ToString("N"));
            request.Headers.Add("x-session-id", context.SessionId);
            request.Headers.Add("x-user-id", context.UserId);

            string curl = await AifaCurlLog.FormatAsync(request, cancellationToken);
            (logRequest ?? Console.Error.WriteLine)($"[AIFA HTTP request; credentials redacted; POSIX shell cURL] {curl}");
        }
        return await base.SendAsync(request, cancellationToken);
    }
}
