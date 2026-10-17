using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Maf.Handoff.Api;

// All workflow comments pass through this formatter, including automatic fallbacks.
public static class JiraCommentHandler
{
    public const string Signature = "[MAF-AI:v1]";
    public const string ProductHeading = "محصول: مسائل و نیازمندی‌ها";
    public const string TechnicalHeading = "ملاحظات فنی";
    private const string EmptySection = "موردی گزارش نشده است.";

    public static string Format(string text, bool scopeChange)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.StartsWith(Signature + "\n", StringComparison.Ordinal) && text.IndexOf("\n\n", StringComparison.Ordinal) is int end && end >= 0)
            text = text[(end + 2)..];
        (string product, string technical) = Sections(text);
        string sender = scopeChange ? "عامل پیاده‌سازی MAF — تغییر دامنهٔ محصول" : "عامل Jira در MAF — بررسی محصول";
        return $"{Signature}\n{sender}\n\n{ProductHeading}\n{product}\n\n{TechnicalHeading}\n{technical}";
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
            bool signed = body?.StartsWith(Signature + "\n", StringComparison.Ordinal) is true ||
                body?.StartsWith(Signature + "\r\n", StringComparison.Ordinal) is true;
            string? normalized = body?.Replace("\r\n", "\n", StringComparison.Ordinal);
            bool legacy = normalized?.StartsWith("گزارش بررسی آمادگی تسک توسط عامل Jira\n", StringComparison.Ordinal) is true ||
                normalized?.StartsWith("گزارش تغییرات خارج از شرح تسک Jira\n", StringComparison.Ordinal) is true;
            comment["mafOrigin"] = signed ? "agent" : legacy ? "legacy_agent" : "external";
        }
    }
}
