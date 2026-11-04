using System.Text;
using XBank.CodeGraph.Contracts;
using XBank.CodeGraph.McpServer.Models;

namespace XBank.CodeGraph.McpServer.Retrieval;

public sealed class ContextRetriever(GraphStore store)
{
    /// <summary>هزینه‌ی هر نوع یال؛ عدد کمتر = ارتباط معنایی نزدیک‌تر</summary>
    private static readonly Dictionary<string, double> EdgeCost = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RESOLVES_TO"] = 0.4,   // DI: اینترفیس ← پیاده‌سازی واقعی
        ["IMPLEMENTS"]  = 0.6,
        ["OVERRIDES"]   = 0.6,
        ["CALLS"]       = 1.0,
        ["INHERITS"]    = 1.0,
        ["CONTAINS"]    = 1.2,
        ["HANDLES"]     = 0.35,
        ["ENDPOINT_OF"] = 0.35,
        ["INSTANTIATES"] = 1.0,
        ["READS"]       = 1.4,
        ["WRITES"]      = 1.4,
        ["USES_TYPE"]   = 2.5,   // گران، تا گراف منفجر نشود
    };

    private const double MaxDistance   = 4.0;
    private const double IncomingPenalty = 1.4;   // یال ورودی (caller) گران‌تر
    private const int    CharsPerToken  = 4;      // تخمین ساده‌ی توکن

    // ---------------------------------------------------------------

    public string Retrieve(string symbolQuery, int maxNodes = 25, int tokenBudget = 5000)
    {
        var seeds = store.FindNodes(symbolQuery, limit: 3);
        if (seeds.Count == 0)
            return $"هیچ نمادی با «{symbolQuery}» در گراف پیدا نشد.";

        // گره‌ی ریشه را با بدنه‌ی کامل می‌خوانیم
        var root = store.GetNode(seeds[0].Id, includeBody: true) ?? seeds[0];

        var picked = Traverse(root, Math.Clamp(maxNodes, 1, 100));
        return Render(root, picked, seeds.Skip(1).ToList(), tokenBudget);
    }

    public string RetrieveById(string id, int maxNodes = 25, int tokenBudget = 5000)
    {
        var root = store.GetNode(id, includeBody: true);
        if (root is null) return $"No symbol with ID '{id}' exists in the graph.";

        var picked = Traverse(root, Math.Clamp(maxNodes, 1, 100));
        return Render(root, picked, [], Math.Clamp(tokenBudget, 250, 20_000));
    }

    // ---------------------------------------------------------------
    // Dijkstra روی گراف کد
    // ---------------------------------------------------------------
    private List<ScoredNode> Traverse(GraphNode root, int maxNodes)
    {
        var best    = new Dictionary<string, double>(StringComparer.Ordinal) { [root.Id] = 0 };
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var result  = new List<ScoredNode>();

        var pq = new PriorityQueue<ScoredNode, double>();
        pq.Enqueue(new ScoredNode(root, 0, "ROOT", 0), 0);

        while (pq.TryDequeue(out var cur, out var dist))
        {
            if (result.Count >= maxNodes) break;
            if (!visited.Add(cur.Node.Id)) continue;      // قبلاً نهایی شده
            if (dist > best[cur.Node.Id]) continue;       // نسخه‌ی کهنه در صف

            result.Add(cur);

            foreach (var (edge, nb, outgoing) in store.GetNeighbors(cur.Node.Id))
            {
                if (visited.Contains(nb.Id)) continue;

                var cost = EdgeCost.GetValueOrDefault(edge.Kind, 2.0);
                if (!outgoing) cost *= IncomingPenalty;

                var nd = dist + cost;
                if (nd > MaxDistance) continue;

                if (!best.TryGetValue(nb.Id, out var old) || nd < old)
                {
                    best[nb.Id] = nd;
                    var label = outgoing ? edge.Kind : $"{edge.Kind}_BY";  // مثلا CALLED_BY
                    pq.Enqueue(new ScoredNode(nb, nd, label, cur.Depth + 1), nd);
                }
            }
        }

        return result;
    }

    // ---------------------------------------------------------------
    // خروجی مارک‌داون فشرده، گروه‌بندی‌شده بر اساس نوع رابطه
    // ---------------------------------------------------------------
    private string Render(GraphNode root, List<ScoredNode> nodes,
                          List<GraphNode> alternatives, int tokenBudget)
    {
        var budget = tokenBudget * CharsPerToken;
        var sb = new StringBuilder(budget);

        sb.AppendLine($"# Context: {root.Name}");
        sb.AppendLine($"`{root.Id}`");
        sb.AppendLine($"- kind: {root.Kind}");
        sb.AppendLine($"- file: {Rel(root.File)}:{root.Line}");
        if (!string.IsNullOrWhiteSpace(root.Signature)) sb.AppendLine($"- signature: `{root.Signature}`");
        sb.AppendLine($"- degree: {root.Degree}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(root.Body))
        {
            sb.AppendLine("## Source");
            sb.AppendLine("csharp");
            sb.AppendLine(Truncate(root.Body, 2000));
            sb.AppendLine("");
            sb.AppendLine();
        }

        var groups = nodes
            .Where(n => n.Node.Id != root.Id)
            .GroupBy(n => n.PathKind)
            .OrderBy(g => g.Min(n => n.Distance));

        foreach (var g in groups)
        {
            var header = $"## {Humanize(g.Key)}";
            if (sb.Length + header.Length > budget) break;
            sb.AppendLine(header);

            foreach (var item in g.OrderBy(n => n.Distance))
            {
                var n = item.Node;
                var line = new StringBuilder();
                line.Append($"- **{n.Name}** `{n.Kind}`");
                if (!string.IsNullOrWhiteSpace(n.Signature))
                    line.Append($" — `{Truncate(n.Signature, 160)}`");
                line.Append($"  \n  <{Rel(n.File)}:{n.Line}> · d={item.Distance:0.0}, degree={n.Degree}");

                if (sb.Length + line.Length > budget)
                {
                    sb.AppendLine("- … (بریده شد به دلیل بودجه‌ی توکن)");
                    return sb.ToString();
                }
                sb.AppendLine(line.ToString());
            }
            sb.AppendLine();
        }

        if (alternatives.Count > 0 && sb.Length < budget)
        {
            sb.AppendLine("## Other matches");
            foreach (var a in alternatives)
                sb.AppendLine($"- {a.Name} `{a.Kind}` — `{a.Id}`");
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------
    private static string Humanize(string kind) => kind switch
    {
        "CALLS"          => "Calls (این کد چه چیزی را صدا می‌زند)",
        "CALLS_BY"       => "Called by (چه کسانی این را صدا می‌زنند)",
        "IMPLEMENTS"     => "Implements",
        "IMPLEMENTS_BY"  => "Implementations",
        "RESOLVES_TO"    => "DI resolves to",
        "RESOLVES_TO_BY" => "Registered for",
        "INHERITS"       => "Base types",
        "INHERITS_BY"    => "Derived types",
        "CONTAINS"       => "Members",
        "CONTAINS_BY"    => "Declaring type",
        "USES_TYPE"      => "Uses types",
        "USES_TYPE_BY"   => "Used by",
        _                => kind
    };

    private static string Rel(string? path) =>
        string.IsNullOrEmpty(path) ? "?" : Path.GetFileName(path);

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + " …";
}
