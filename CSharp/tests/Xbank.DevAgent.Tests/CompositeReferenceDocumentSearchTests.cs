using Xbank.DevAgent.Core;

namespace Xbank.DevAgent.Tests;

public sealed class CompositeReferenceDocumentSearchTests
{
    [Fact]
    public async Task IncludesResultsFromEveryConfiguredSource()
    {
        CompositeReferenceDocumentSearch search = new(
        [
            ("Qdrant", new StubSearch("attached result")),
            ("Cognia", new StubSearch("organization result")),
        ]);

        string result = await search.SearchAsync("policy", 3);

        Assert.Contains("## Qdrant\nattached result", result, StringComparison.Ordinal);
        Assert.Contains("## Cognia\norganization result", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreservesOtherResultsWhenOneSourceFails()
    {
        CompositeReferenceDocumentSearch search = new(
        [
            ("Qdrant", new ThrowingSearch()),
            ("Cognia", new StubSearch("organization result")),
        ]);

        string result = await search.SearchAsync("policy", 3);

        Assert.Contains("## Qdrant\nSearch failed: unavailable", result, StringComparison.Ordinal);
        Assert.Contains("## Cognia\norganization result", result, StringComparison.Ordinal);
    }

    private sealed class StubSearch(string result) : IReferenceDocumentSearch
    {
        public Task<string> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class ThrowingSearch : IReferenceDocumentSearch
    {
        public Task<string> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("unavailable");
    }
}
