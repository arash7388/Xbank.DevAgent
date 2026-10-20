namespace Maf.Handoff.Core;

public sealed class RunConnectivityCheck(HttpClient httpClient)
{
    public async Task CheckAsync(IEnumerable<string> modelEndpoints, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await Task.WhenAll(modelEndpoints.Distinct(StringComparer.OrdinalIgnoreCase).Select(async endpoint =>
        {
            try
            {
                // Any HTTP response (including 401/405) proves connectivity. No credentials
                // or inference requests are needed to check whether the provider is reachable.
                using HttpRequestMessage request = new(HttpMethod.Head, endpoint);
                using HttpResponseMessage response = await httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            }
            catch (Exception exception) when ((exception is HttpRequestException or OperationCanceledException) &&
                !cancellationToken.IsCancellationRequested)
            {
                throw new RunConnectivityException(
                    $"Cannot reach model provider '{new Uri(endpoint).Host}'. Check the internet connection, VPN or proxy on the machine running MAF, then try again.",
                    exception);
            }
        }));
    }
}

public sealed class RunConnectivityException(string message, Exception innerException) : Exception(message, innerException);
