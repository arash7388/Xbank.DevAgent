using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using Maf.Handoff.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Maf.Handoff.Api;

/// <summary>
/// Indexes one set of user-supplied references in a temporary Qdrant collection. Source files are
/// processed in memory only; the collection is deleted after use or after its short safety lifetime.
/// </summary>
public sealed class ReferenceDocumentService(
    IHttpClientFactory httpClientFactory,
    IOptions<RagOptions> options)
{
    private const int MaxFiles = 50;
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const long MaxTotalBytes = 25 * 1024 * 1024;
    private const int MaxDocumentCharacters = 400_000;
    private const int ChunkCharacters = 2_000;
    private const int ChunkOverlapCharacters = 300;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".json", ".jsonc", ".xml", ".yml", ".yaml", ".toml",
        ".html", ".htm", ".docx", ".pdf", ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".java",
        ".sql", ".ps1", ".sh",
    };
    private readonly ConcurrentDictionary<Guid, ReferenceCollection> _collections = new();

    public async Task<ReferenceCollectionInfo> CreateAsync(
        IFormFileCollection files,
        string embeddingProvider,
        string embeddingApiKey,
        string qdrantUrl,
        string qdrantApiKey,
        CancellationToken cancellationToken)
    {
        if (files.Count is 0 or > MaxFiles)
        {
            throw new ArgumentException($"Attach between 1 and {MaxFiles} supported files.");
        }

        EmbeddingConfiguration embedding = CreateEmbeddingConfiguration(embeddingProvider, embeddingApiKey);
        QdrantConfiguration qdrant = CreateQdrantConfiguration(qdrantUrl, qdrantApiKey);

        long totalBytes = 0;
        List<ReferenceChunk> chunks = [];
        foreach (IFormFile file in files)
        {
            ValidateFile(file);
            totalBytes += file.Length;
            if (totalBytes > MaxTotalBytes)
            {
                throw new ArgumentException($"Attached files exceed the {MaxTotalBytes / 1024 / 1024} MB total limit.");
            }

            string path = NormalizeRelativePath(file.FileName);
            string text = await ExtractTextAsync(file, cancellationToken);
            int chunkIndex = 0;
            foreach (string chunk in Chunk(text))
            {
                chunks.Add(new(Guid.NewGuid(), path, chunkIndex++, chunk));
            }
        }

        if (chunks.Count == 0)
        {
            throw new ArgumentException("The attached files did not contain indexable text.");
        }

        List<float[]> vectors = [];
        foreach (ReferenceChunk[] batch in chunks.Chunk(32))
        {
            vectors.AddRange(await EmbedAsync(batch.Select(chunk => chunk.Text).ToArray(), embedding, cancellationToken));
        }

        if (vectors.Count != chunks.Count || vectors.Count == 0)
        {
            throw new InvalidOperationException("The embedding service returned an incomplete embedding response.");
        }

        Guid id = Guid.NewGuid();
        string collectionName = $"maf_refs_{id:N}";
        try
        {
            await CreateQdrantCollectionAsync(qdrant, collectionName, vectors[0].Length, cancellationToken);
            await UpsertAsync(qdrant, collectionName, chunks, vectors, cancellationToken);
        }
        catch
        {
            await DeleteQdrantCollectionAsync(qdrant, collectionName, CancellationToken.None);
            throw;
        }

        ReferenceCollection collection = new(id, collectionName, embedding, qdrant, chunks, files.Count, chunks.Count);
        if (!_collections.TryAdd(id, collection))
        {
            await DeleteQdrantCollectionAsync(qdrant, collectionName, CancellationToken.None);
            throw new InvalidOperationException("Unable to allocate the reference-document collection.");
        }

        _ = DeleteAfterLifetimeAsync(id, TimeSpan.FromMinutes(Math.Clamp(options.Value.CollectionLifetimeMinutes, 5, 120)));
        return new(id, files.Count, chunks.Count, files.Select(file => NormalizeRelativePath(file.FileName)).ToArray());
    }

    public bool Exists(Guid id) => _collections.ContainsKey(id);

    public IReferenceDocumentSearch GetSearch(Guid id) => _collections.TryGetValue(id, out ReferenceCollection? collection)
        ? new QdrantReferenceDocumentSearch(this, collection)
        : throw new ArgumentException("The attached document collection was not found or has expired.");

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (_collections.TryRemove(id, out ReferenceCollection? collection))
        {
            await DeleteQdrantCollectionAsync(collection.Qdrant, collection.QdrantCollectionName, cancellationToken);
        }
    }

    private async Task<string> SearchAsync(
        ReferenceCollection collection,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2_000)
        {
            return "Reference-document search requires a query between 1 and 2,000 characters.";
        }

        DocumentSearchRoute route = DocumentSearchRouter.Route(query);
        return route.Method == DocumentSearchMethod.Exact
            ? ExactSearch(collection, query, maxResults)
            : await SemanticSearchAsync(collection, query, maxResults, cancellationToken);
    }

    private static string ExactSearch(ReferenceCollection collection, string query, int maxResults)
    {
        string phrase = query.Trim().Trim('"', '`').Trim();
        string[] terms = phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int limit = Math.Clamp(maxResults, 1, 8);
        ReferenceChunk[] matches = collection.Chunks
            .Select(chunk => new
            {
                Chunk = chunk,
                FileHits = CountOccurrences(chunk.FileName, phrase),
                PhraseHits = CountOccurrences(chunk.Text, phrase),
                TermHits = terms.Sum(term => CountOccurrences(chunk.Text, term)),
            })
            .Where(match => match.FileHits > 0 || match.PhraseHits > 0 ||
                (terms.Length > 0 && terms.All(term => ContainsOrdinalIgnoreCase(match.Chunk.Text, term))))
            .OrderByDescending(match => match.FileHits * 10_000 + match.PhraseHits * 1_000 + match.TermHits)
            .ThenBy(match => match.Chunk.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Chunk.Index)
            .Take(limit)
            .Select(match => match.Chunk)
            .ToArray();

        return FormatResults(matches, "exact keyword");
    }

    private async Task<string> SemanticSearchAsync(
        ReferenceCollection collection,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        float[] vector = (await EmbedAsync([query.Trim()], collection.Embedding, cancellationToken))[0];
        using JsonDocument response = await QdrantJsonAsync(
            HttpMethod.Post,
            $"collections/{collection.QdrantCollectionName}/points/query",
            new { query = vector, limit = Math.Clamp(maxResults, 1, 8), with_payload = true, with_vector = false },
            cancellationToken,
            collection.Qdrant);
        JsonElement result = response.RootElement.GetProperty("result");
        JsonElement points = result.ValueKind == JsonValueKind.Array
            ? result
            : result.GetProperty("points");
        List<ReferenceChunk> matches = [];
        foreach (JsonElement point in points.EnumerateArray())
        {
            JsonElement payload = point.GetProperty("payload");
            string file = payload.GetProperty("file").GetString() ?? "unknown file";
            int chunk = payload.GetProperty("chunk").GetInt32();
            string text = payload.GetProperty("text").GetString() ?? string.Empty;
            matches.Add(new(Guid.Empty, file, chunk, text));
        }

        return FormatResults(matches, "semantic");
    }

    private static string FormatResults(IReadOnlyList<ReferenceChunk> matches, string method)
    {
        if (matches.Count == 0)
        {
            return $"No relevant attached-document excerpts were found using {method} search.";
        }

        StringBuilder output = new($"[Search method: {method}]\n");
        for (int index = 0; index < matches.Count; index++)
        {
            ReferenceChunk match = matches[index];
            output.Append("[Reference ").Append(index + 1).Append(": ").Append(match.FileName)
                .Append(", chunk ").Append(match.Index + 1).AppendLine("]")
                .AppendLine(match.Text.Trim()).AppendLine();
        }

        return output.ToString();
    }

    private static int CountOccurrences(string source, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        int count = 0;
        for (int start = 0; ; start += value.Length)
        {
            int found = source.IndexOf(value, start, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return count;
            }

            count++;
        }
    }

    private static bool ContainsOrdinalIgnoreCase(string source, string value) =>
        source.Contains(value, StringComparison.OrdinalIgnoreCase);

    private async Task<List<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs,
        EmbeddingConfiguration embedding,
        CancellationToken cancellationToken)
    {
        using HttpClient client = httpClientFactory.CreateClient("embeddings");
        string path = embedding.Provider == EmbeddingProvider.Ollama ? "api/embed" : "embeddings";
        using HttpRequestMessage request = new(HttpMethod.Post, BuildUri(embedding.Endpoint, path));
        if (!string.IsNullOrWhiteSpace(embedding.ApiKey))
        {
            request.Headers.Authorization = new("Bearer", embedding.ApiKey);
        }

        request.Content = JsonContent(new { model = embedding.Model, input = inputs });
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Embedding request");
        using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (embedding.Provider == EmbeddingProvider.Ollama)
        {
            return payload.RootElement.GetProperty("embeddings")
                .EnumerateArray()
                .Select(item => item.EnumerateArray().Select(value => value.GetSingle()).ToArray())
                .ToList();
        }

        return payload.RootElement.GetProperty("data")
            .EnumerateArray()
            .OrderBy(item => item.GetProperty("index").GetInt32())
            .Select(item => item.GetProperty("embedding").EnumerateArray().Select(value => value.GetSingle()).ToArray())
            .ToList();
    }

    private async Task CreateQdrantCollectionAsync(
        QdrantConfiguration qdrant,
        string collectionName,
        int vectorSize,
        CancellationToken cancellationToken)
    {
        using JsonDocument ignored = await QdrantJsonAsync(
            HttpMethod.Put,
            $"collections/{collectionName}",
            new { vectors = new { size = vectorSize, distance = "Cosine" } },
            cancellationToken,
            qdrant);
    }

    private async Task UpsertAsync(
        QdrantConfiguration qdrant,
        string collectionName,
        IReadOnlyList<ReferenceChunk> chunks,
        IReadOnlyList<float[]> vectors,
        CancellationToken cancellationToken)
    {
        IEnumerable<(ReferenceChunk Chunk, float[] Vector)> points = chunks.Zip(vectors);
        foreach ((ReferenceChunk chunk, float[] vector)[] batch in points.Chunk(64))
        {
            object body = new
            {
                points = batch.Select(item => new
                {
                    id = item.chunk.Id,
                    vector = item.vector,
                    payload = new { file = item.chunk.FileName, chunk = item.chunk.Index, text = item.chunk.Text },
                }),
            };
            using JsonDocument ignored = await QdrantJsonAsync(
                HttpMethod.Put,
                $"collections/{collectionName}/points?wait=true",
                body,
                cancellationToken,
                qdrant);
        }
    }

    private async Task<JsonDocument> QdrantJsonAsync(
        HttpMethod method,
        string path,
        object body,
        CancellationToken cancellationToken,
        QdrantConfiguration qdrant)
    {
        using HttpClient client = httpClientFactory.CreateClient("qdrant");
        using HttpRequestMessage request = new(method, BuildUri(qdrant.Url, path))
        {
            Content = JsonContent(body),
        };
        if (!string.IsNullOrWhiteSpace(qdrant.ApiKey))
        {
            request.Headers.Add("api-key", qdrant.ApiKey);
        }

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Qdrant request");
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private async Task DeleteQdrantCollectionAsync(
        QdrantConfiguration qdrant,
        string collectionName,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpClient client = httpClientFactory.CreateClient("qdrant");
            using HttpRequestMessage request = new(HttpMethod.Delete, BuildUri(qdrant.Url, $"collections/{collectionName}"));
            if (!string.IsNullOrWhiteSpace(qdrant.ApiKey))
            {
                request.Headers.Add("api-key", qdrant.ApiKey);
            }

            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                await EnsureSuccessAsync(response, "Qdrant cleanup request");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The short-lived collection may be removed by the scheduled cleanup later.
        }
    }

    private async Task DeleteAfterLifetimeAsync(Guid id, TimeSpan lifetime)
    {
        await Task.Delay(lifetime);
        await DeleteAsync(id);
    }

    internal static async Task<string> ExtractTextAsync(IFormFile file, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(file.FileName);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            await using MemoryStream pdfStream = new();
            await file.CopyToAsync(pdfStream, cancellationToken);
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(pdfStream.ToArray());
            StringBuilder text = new();
            foreach (var page in pdf.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                text.AppendLine(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page));
                if (text.Length >= MaxDocumentCharacters) break;
            }
            if (string.IsNullOrWhiteSpace(text.ToString())) throw new ArgumentException("PDF has no extractable text; scanned PDFs require OCR.");
            return LimitText(text.ToString());
        }
        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            await using MemoryStream stream = new();
            await file.CopyToAsync(stream, cancellationToken);
            stream.Position = 0;
            using WordprocessingDocument document = WordprocessingDocument.Open(stream, false);
            string text = document.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty;
            return LimitText(text);
        }

        await using Stream input = file.OpenReadStream();
        using StreamReader reader = new(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        return LimitText(await reader.ReadToEndAsync(cancellationToken));
    }

    private static IEnumerable<string> Chunk(string text)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        for (int start = 0; start < normalized.Length;)
        {
            int length = Math.Min(ChunkCharacters, normalized.Length - start);
            int end = start + length;
            if (end < normalized.Length)
            {
                int breakAt = normalized.LastIndexOf('\n', end - 1, Math.Min(length, 500));
                if (breakAt > start + 250)
                {
                    end = breakAt;
                }
            }

            string chunk = normalized[start..end].Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                yield return chunk;
            }

            if (end >= normalized.Length)
            {
                yield break;
            }

            start = Math.Max(end - ChunkOverlapCharacters, start + 1);
        }
    }

    internal static void ValidateFile(IFormFile file)
    {
        if (file.Length is <= 0 or > MaxFileBytes)
        {
            throw new ArgumentException($"'{file.FileName}' must contain text and be no larger than {MaxFileBytes / 1024 / 1024} MB.");
        }

        if (!SupportedExtensions.Contains(Path.GetExtension(file.FileName)))
        {
            throw new ArgumentException($"'{file.FileName}' is not a supported reference-document format.");
        }

        _ = NormalizeRelativePath(file.FileName);
    }

    private static string NormalizeRelativePath(string path)
    {
        string normalized = path.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 240 || Path.IsPathRooted(path) ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Attached document names must be safe relative paths.");
        }

        return normalized;
    }

    private static string LimitText(string text) => text.Length <= MaxDocumentCharacters
        ? text
        : text[..MaxDocumentCharacters];

    private static Uri BuildUri(string baseUrl, string path) => new(
        $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}", UriKind.Absolute);

    private static StringContent JsonContent(object value) => new(
        JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string details = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"{operation} failed ({(int)response.StatusCode}): {details[..Math.Min(details.Length, 500)]}");
    }

    private EmbeddingConfiguration CreateEmbeddingConfiguration(string provider, string apiKey)
    {
        return provider.Trim().ToLowerInvariant() switch
        {
            "ollama" => new(
                EmbeddingProvider.Ollama,
                options.Value.OllamaEndpoint,
                options.Value.OllamaEmbeddingModel,
                null),
            "openai" when !string.IsNullOrWhiteSpace(apiKey) => new(
                EmbeddingProvider.OpenAi,
                options.Value.OpenAiEmbeddingEndpoint,
                options.Value.OpenAiEmbeddingModel,
                apiKey.Trim()),
            "openai" => throw new ArgumentException("An OpenAI API key is required when OpenAI embeddings are selected."),
            _ => throw new ArgumentException("The embedding provider must be either 'ollama' or 'openai'."),
        };
    }

    private QdrantConfiguration CreateQdrantConfiguration(string url, string apiKey)
    {
        string effectiveUrl = string.IsNullOrWhiteSpace(url) ? options.Value.QdrantUrl : url.Trim();
        if (!Uri.TryCreate(effectiveUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Qdrant URL must be an absolute HTTP(S) URL without credentials, query, or fragment.");
        }

        bool isLoopback = uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        bool isQdrantCloud = uri.Host.EndsWith(".cloud.qdrant.io", StringComparison.OrdinalIgnoreCase);
        if ((!isLoopback && !isQdrantCloud) || (!isLoopback && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Qdrant must use HTTPS Qdrant Cloud or a local loopback endpoint.");
        }

        string effectiveApiKey = string.IsNullOrWhiteSpace(apiKey)
            ? options.Value.QdrantApiKey ?? string.Empty
            : apiKey.Trim();
        if (isQdrantCloud && string.IsNullOrWhiteSpace(effectiveApiKey))
        {
            throw new ArgumentException("A Qdrant Cloud database API key is required for a Qdrant Cloud endpoint.");
        }

        return new(uri.GetLeftPart(UriPartial.Authority), effectiveApiKey);
    }

    private enum EmbeddingProvider
    {
        OpenAi,
        Ollama,
    }

    private sealed record EmbeddingConfiguration(
        EmbeddingProvider Provider,
        string Endpoint,
        string Model,
        string? ApiKey);

    private sealed record ReferenceCollection(
        Guid Id,
        string QdrantCollectionName,
        EmbeddingConfiguration Embedding,
        QdrantConfiguration Qdrant,
        IReadOnlyList<ReferenceChunk> Chunks,
        int FileCount,
        int ChunkCount);

    private sealed record QdrantConfiguration(string Url, string ApiKey);

    private sealed record ReferenceChunk(Guid Id, string FileName, int Index, string Text);

    private sealed class QdrantReferenceDocumentSearch(
        ReferenceDocumentService owner,
        ReferenceCollection collection) : IReferenceDocumentSearch
    {
        public Task<string> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            owner.SearchAsync(collection, query, maxResults, cancellationToken);
    }
}

public sealed record ReferenceCollectionInfo(
    Guid Id,
    int FileCount,
    int ChunkCount,
    IReadOnlyList<string> Files);
