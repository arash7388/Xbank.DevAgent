namespace Maf.Handoff.Api;

public sealed class RagOptions
{
    public const string SectionName = "Rag";

    public string QdrantUrl { get; init; } = "http://127.0.0.1:6333";

    public string? QdrantApiKey { get; init; }

    public string EmbeddingEndpoint { get; init; } = "https://api.openai.com/v1";

    public string EmbeddingModel { get; init; } = "text-embedding-3-small";

    public int CollectionLifetimeMinutes { get; init; } = 30;
}
