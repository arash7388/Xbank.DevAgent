namespace Xbank.DevAgent.Api;

public sealed class RagOptions
{
    public const string SectionName = "Rag";

    public string QdrantUrl { get; init; } = "http://127.0.0.1:6333";

    public string? QdrantApiKey { get; init; }

    public string OpenAiEmbeddingEndpoint { get; init; } = "https://api.openai.com/v1";

    public string OpenAiEmbeddingModel { get; init; } = "text-embedding-3-small";

    public string OllamaEndpoint { get; init; } = "http://127.0.0.1:11434";

    public string OllamaEmbeddingModel { get; init; } = "embeddinggemma";

    public int CollectionLifetimeMinutes { get; init; } = 30;
}
