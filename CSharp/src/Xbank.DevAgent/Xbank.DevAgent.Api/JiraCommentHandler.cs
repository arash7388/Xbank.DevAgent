using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xbank.DevAgent.Api;

// All workflow comments pass through this formatter, including automatic fallbacks.
public static class JiraCommentHandler
{
    public const int MaxCommentLength = 1000;
    public const string Signature = "Developer Agent Points";
    private const string PreviousSignature = "[MAF-AI:v1]";

    private static bool HasSignature(string? text) =>
        text?.StartsWith(Signature + "\n", StringComparison.Ordinal) is true ||
        text?.StartsWith(PreviousSignature + "\n", StringComparison.Ordinal) is true;
    public const string ProductHeading = "محصول: مسائل و نیازمندی‌ها";
    public const string TechnicalHeading = "ملاحظات فنی";
    private const string EmptySection = "موردی گزارش نشده است.";

    public static string Format(string text, bool scopeChange, string? reporterMention = null)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        string suffix = string.IsNullOrWhiteSpace(reporterMention) ? "" : "\n\n" + reporterMention;
        if (suffix.Length > 0 && text.EndsWith(suffix, StringComparison.Ordinal))
            text = text[..^suffix.Length];
        if (HasSignature(text) && text.IndexOf("\n\n", StringComparison.Ordinal) is int end && end >= 0)
            text = text[(end + 2)..];
        (string product, string technical) = Sections(text);
        string sender = scopeChange ? "عامل پیاده‌سازی Xbank.DevAgent — تغییر دامنهٔ محصول" : "عامل Jira در Xbank.DevAgent — بررسی محصول";
        string prefix = $"{Signature}\n{sender}\n\n{ProductHeading}\n";
        string separator = $"\n\n{TechnicalHeading}\n";
        int budget = MaxCommentLength - prefix.Length - separator.Length - suffix.Length;
        // Preserve both sections, sharing space only when both need it.
        int productBudget = Math.Clamp(budget - technical.Length, budget / 2, budget);
        productBudget = Math.Min(product.Length, productBudget);
        product = Limit(product, productBudget);
        technical = Limit(technical, budget - product.Length);
        return prefix + product + separator + technical + suffix;
    }

    private static string Limit(string text, int length)
    {
        if (text.Length <= length) return text;
        int end = length - 1;
        if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end].TrimEnd() + "…";
    }

    private static (string Product, string Technical) Sections(string text)
    {
        StringBuilder product = new(), technical = new();
        StringBuilder current = product;
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string heading = line.Trim().Trim('#', '*', ' ', ':');
            if (heading == ProductHeading) { current = product; continue; }
            if (heading == TechnicalHeading) { current = technical; continue; }
            current.AppendLine(line);
        }
        return (Value(product), Value(technical));
        static string Value(StringBuilder section) => section.ToString().Trim() is { Length: > 0 } value ? value : EmptySection;
    }

    // Preserve author/body/pagination and selection filtering. A signature labels
    // agent-generated evidence; it is not proof of authority or a Product decision.
    public static JsonElement Annotate(JsonElement response)
    {
        JsonNode? root = JsonNode.Parse(response.GetRawText());
        if (root is JsonObject obj)
        {
            AnnotateComments(obj["comments"] as JsonArray);
            if (obj["fields"] is JsonObject fields && fields["comment"] is JsonObject comments)
                AnnotateComments(comments["comments"] as JsonArray);
        }
        return JsonSerializer.SerializeToElement(root);
    }

    private static void AnnotateComments(JsonArray? comments)
    {
        if (comments is null) return;
        foreach (JsonObject comment in comments.OfType<JsonObject>())
        {
            string? body = comment["body"] is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;
            string? normalized = body?.Replace("\r\n", "\n", StringComparison.Ordinal);
            bool signed = HasSignature(normalized);
            bool legacy = normalized?.StartsWith("گزارش بررسی آمادگی تسک توسط عامل Jira\n", StringComparison.Ordinal) is true ||
                normalized?.StartsWith("گزارش تغییرات خارج از شرح تسک Jira\n", StringComparison.Ordinal) is true;
            comment["mafOrigin"] = signed ? "agent" : legacy ? "legacy_agent" : "external";
        }
    }
}
