using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Maf.Handoff.Api;

public sealed record DeepSeekModelPrice(
    string Model,
    decimal CachedInputUsdPerMillion,
    decimal InputUsdPerMillion,
    decimal OutputUsdPerMillion);

public sealed record DeepSeekPricingSnapshot(
    string Provider,
    string PriceBand,
    DateTimeOffset EffectiveAt,
    DateTimeOffset FetchedAt,
    string SourceUrl,
    IReadOnlyList<DeepSeekModelPrice> Models);

public sealed class DeepSeekPricingService(HttpClient httpClient)
{
    public const string PricingUrl = "https://api-docs.deepseek.com/quick_start/pricing/";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private PriceTable? _cached;

    public async Task<DeepSeekPricingSnapshot> GetCurrentAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PriceTable table = _cached is { } cached && now - cached.FetchedAt < CacheDuration
            ? cached
            : await RefreshAsync(now, cancellationToken);
        bool peak = IsPeak(now);
        IReadOnlyList<DeepSeekModelPrice> models = table.Models.Select(model => new DeepSeekModelPrice(
            model.Model,
            peak ? model.PeakCachedInput : model.OffPeakCachedInput,
            peak ? model.PeakInput : model.OffPeakInput,
            peak ? model.PeakOutput : model.OffPeakOutput)).ToArray();
        return new("deepseek", peak ? "peak" : "off-peak", now, table.FetchedAt, PricingUrl, models);
    }

    internal static bool IsPeak(DateTimeOffset utc) =>
        utc.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday &&
        (utc.Hour is >= 1 and < 4 || utc.Hour is >= 6 and < 10);

    private async Task<PriceTable> RefreshAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } cached && now - cached.FetchedAt < CacheDuration)
            {
                return cached;
            }

            string html = await httpClient.GetStringAsync(PricingUrl, cancellationToken);
            _cached = Parse(html, now);
            return _cached;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    internal static PriceTable Parse(string html, DateTimeOffset fetchedAt)
    {
        string tableHtml = Regex.Match(html, @"<table[^>]*>.*?</table>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Value;
        List<string[]> rows = Regex.Matches(tableHtml, @"<tr[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Select(match => Regex.Matches(match.Groups[1].Value, @"<td[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                .Select(cell => NormalizeCell(cell.Groups[1].Value)).ToArray())
            .Where(row => row.Length > 0)
            .ToList();
        string[] header = rows.FirstOrDefault(row => row.Contains("MODEL", StringComparer.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("DeepSeek's pricing table did not contain a model header.");
        string[] models = header.Where(value => value.StartsWith("deepseek-", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (models.Length == 0)
        {
            throw new InvalidOperationException("DeepSeek's pricing table did not list any models.");
        }

        Dictionary<string, decimal[]> rates = new(StringComparer.OrdinalIgnoreCase);
        string? category = null;
        foreach (string[] row in rows)
        {
            category = row.FirstOrDefault(value => value.Contains("CACHE HIT", StringComparison.OrdinalIgnoreCase)) is not null ? "cached" :
                row.FirstOrDefault(value => value.Contains("CACHE MISS", StringComparison.OrdinalIgnoreCase)) is not null ? "input" :
                row.FirstOrDefault(value => value.Contains("OUTPUT TOKENS", StringComparison.OrdinalIgnoreCase)) is not null ? "output" : category;
            string? band = row.FirstOrDefault(value => value.Equals("PEAK", StringComparison.OrdinalIgnoreCase) || value.Equals("OFF-PEAK", StringComparison.OrdinalIgnoreCase));
            decimal[] prices = row.Where(value => value.StartsWith('$')).Select(ParsePrice).ToArray();
            if (category is not null && band is not null && prices.Length >= models.Length)
            {
                rates[$"{category}:{band}"] = prices[^models.Length..];
            }
        }

        decimal[] Required(string key) => rates.TryGetValue(key, out decimal[]? values)
            ? values
            : throw new InvalidOperationException($"DeepSeek's pricing table did not contain '{key}'.");
        decimal[] cachedOffPeak = Required("cached:OFF-PEAK");
        decimal[] cachedPeak = Required("cached:PEAK");
        decimal[] inputOffPeak = Required("input:OFF-PEAK");
        decimal[] inputPeak = Required("input:PEAK");
        decimal[] outputOffPeak = Required("output:OFF-PEAK");
        decimal[] outputPeak = Required("output:PEAK");

        return new(fetchedAt, models.Select((model, index) => new ModelPriceTable(
            model, cachedOffPeak[index], cachedPeak[index], inputOffPeak[index], inputPeak[index],
            outputOffPeak[index], outputPeak[index])).ToArray());
    }

    private static string NormalizeCell(string html) => WebUtility.HtmlDecode(
        Regex.Replace(Regex.Replace(html, @"<br\s*/?>", " ", RegexOptions.IgnoreCase), "<[^>]+>", string.Empty))
        .Trim();

    private static decimal ParsePrice(string value) =>
        decimal.Parse(value.TrimStart('$'), NumberStyles.Number, CultureInfo.InvariantCulture);

    internal sealed record PriceTable(DateTimeOffset FetchedAt, IReadOnlyList<ModelPriceTable> Models);
    internal sealed record ModelPriceTable(
        string Model, decimal OffPeakCachedInput, decimal PeakCachedInput,
        decimal OffPeakInput, decimal PeakInput, decimal OffPeakOutput, decimal PeakOutput);
}
