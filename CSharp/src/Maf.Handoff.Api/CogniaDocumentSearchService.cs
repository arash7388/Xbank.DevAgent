using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maf.Handoff.Core;

namespace Maf.Handoff.Api;

/// <summary>Creates per-run searches over Cognia's remote knowledge search API.</summary>
public sealed class CogniaDocumentSearchService(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public IReferenceDocumentSearch CreateSearch(CogniaSearchConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new CogniaDocumentSearch(httpClientFactory, configuration.NormalizeAndValidate());
    }

    private sealed class CogniaDocumentSearch(
        IHttpClientFactory httpClientFactory,
        CogniaSearchConfiguration configuration) : IReferenceDocumentSearch
    {
        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private string? _accessToken;
        private DateTimeOffset _tokenExpiresAt;

        public async Task<string> SearchAsync(
            string query,
            int maxResults,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length > 2_000)
            {
                return "Cognia search requires a query between 1 and 2,000 characters.";
            }

            string token = await GetAccessTokenAsync(cancellationToken);
            using HttpResponseMessage response = await SendSearchAsync(query.Trim(), maxResults, token, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                InvalidateAccessToken(token);
                token = await GetAccessTokenAsync(cancellationToken);
                using HttpResponseMessage retry = await SendSearchAsync(query.Trim(), maxResults, token, cancellationToken);
                return await ReadSearchResponseAsync(retry, cancellationToken);
            }

            return await ReadSearchResponseAsync(response, cancellationToken);
        }

        private async Task<HttpResponseMessage> SendSearchAsync(
            string query,
            int maxResults,
            string accessToken,
            CancellationToken cancellationToken)
        {
            HttpClient client = httpClientFactory.CreateClient("cognia");
            HttpRequestMessage request = new(HttpMethod.Post, BuildUri(configuration.Endpoint, "api/engine/search"))
            {
                Content = JsonContent.Create(new
                {
                    query,
                    knowledgeBaseIds = configuration.KnowledgeBaseIds,
                    limit = Math.Clamp(maxResults, 1, 8),
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            try
            {
                return await client.SendAsync(request, cancellationToken);
            }
            finally
            {
                request.Dispose();
            }
        }

        private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            if (TokenIsUsable())
            {
                return _accessToken!;
            }

            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                if (TokenIsUsable())
                {
                    return _accessToken!;
                }

                using HttpClient client = httpClientFactory.CreateClient("cognia");
                using HttpRequestMessage request = new(
                    HttpMethod.Post,
                    BuildUri(configuration.Endpoint, "api/access/client-auth/token"))
                {
                    Content = JsonContent.Create(new
                    {
                        clientId = configuration.ClientId,
                        clientSecret = configuration.ClientSecret,
                    }),
                };
                using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
                await EnsureSuccessAsync(response, "Cognia authentication", cancellationToken);
                CogniaAuthenticationResponse? authentication = await response.Content
                    .ReadFromJsonAsync<CogniaAuthenticationResponse>(ResponseJsonOptions, cancellationToken);
                if (authentication is null || string.IsNullOrWhiteSpace(authentication.AccessToken))
                {
                    throw new HttpRequestException("Cognia authentication returned an invalid response.");
                }

                _accessToken = authentication.AccessToken;
                long usableSeconds = Math.Max(1, authentication.ExpiresIn - 30);
                _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(usableSeconds);
                return _accessToken;
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        private bool TokenIsUsable() =>
            !string.IsNullOrWhiteSpace(_accessToken) && DateTimeOffset.UtcNow < _tokenExpiresAt;

        private void InvalidateAccessToken(string rejectedToken)
        {
            if (string.Equals(_accessToken, rejectedToken, StringComparison.Ordinal))
            {
                _accessToken = null;
                _tokenExpiresAt = default;
            }
        }

        private static async Task<string> ReadSearchResponseAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            using (response)
            {
                await EnsureSuccessAsync(response, "Cognia search", cancellationToken);
                CogniaSearchResponse? search = await response.Content
                    .ReadFromJsonAsync<CogniaSearchResponse>(ResponseJsonOptions, cancellationToken);
                if (search?.Items is null)
                {
                    throw new HttpRequestException("Cognia search returned an invalid response.");
                }

                if (search.Items.Count == 0)
                {
                    return "No relevant Cognia knowledge excerpts were found.";
                }

                StringBuilder output = new("[Search method: Cognia hybrid]\n");
                foreach (CogniaSearchResult item in search.Items)
                {
                    output.Append("[Cognia knowledge base ").Append(item.KnowledgeBaseId)
                        .Append(", knowledge ").Append(item.KnowledgeId)
                        .Append(", revision ").Append(item.RevisionNumber)
                        .Append(", chunk ").Append(item.ChunkIndex + 1)
                        .Append(", relevance ").Append(item.RelevanceScore.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                        .AppendLine("]")
                        .AppendLine(item.ChunkText.Trim()).AppendLine();
                }

                return output.ToString();
            }
        }

        private static async Task EnsureSuccessAsync(
            HttpResponseMessage response,
            string operation,
            CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            string detail = string.Empty;
            try
            {
                using JsonDocument problem = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
                if (problem.RootElement.TryGetProperty("detail", out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    detail = $" {value.GetString()}";
                }
            }
            catch (JsonException)
            {
                // Cognia did not return a problem-details body.
            }

            throw new HttpRequestException(
                $"{operation} failed with HTTP {(int)response.StatusCode}.{detail}".TrimEnd(),
                null,
                response.StatusCode);
        }

        private static Uri BuildUri(string endpoint, string path) =>
            new(new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + '/', UriKind.Absolute), path);
    }

    private sealed record CogniaAuthenticationResponse(
        string TokenType,
        string AccessToken,
        long ExpiresIn);

    private sealed record CogniaSearchResponse(IReadOnlyList<CogniaSearchResult> Items);

    private sealed record CogniaSearchResult(
        int Rank,
        double RelevanceScore,
        long KnowledgeBaseId,
        long KnowledgeId,
        long RevisionId,
        int RevisionNumber,
        long ChunkId,
        int ChunkIndex,
        string ChunkText);
}

public sealed record CogniaSearchConfiguration(
    string Endpoint,
    string ClientId,
    string ClientSecret,
    IReadOnlyList<long> KnowledgeBaseIds)
{
    public CogniaSearchConfiguration NormalizeAndValidate()
    {
        if (!Uri.TryCreate(Endpoint?.Trim(), UriKind.Absolute, out Uri? endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException("Cognia endpoint must be an absolute HTTP(S) URL without credentials, query, or fragment.");
        }

        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new ArgumentException("Cognia client ID and client secret are required.");
        }

        long[] knowledgeBaseIds = KnowledgeBaseIds?.Distinct().ToArray() ?? [];
        if (knowledgeBaseIds.Length == 0 || knowledgeBaseIds.Any(id => id <= 0))
        {
            throw new ArgumentException("At least one positive Cognia knowledge base ID is required.");
        }

        string normalizedEndpoint = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return this with
        {
            Endpoint = normalizedEndpoint,
            ClientId = ClientId.Trim(),
            ClientSecret = ClientSecret.Trim(),
            KnowledgeBaseIds = knowledgeBaseIds,
        };
    }
}
