using XBank.CodeGraph.Model;
using XBank.CodeGraph.Contracts;
using XBank.CodeGraph.Storage;
using System.Text;

namespace XBank.CodeGraph.Retrieval;

public sealed class RetrieveOptions
{
    public double MaxDistance { get; init; } = 2.2;
    public int MaxNodes { get; init; } = 60;
    public int TokenBudget { get; init; } = 6000;
    /// <summary>گره‌هایی با درجه‌ی بالاتر از این، مسیر عبور نمی‌شوند (مثل DbContext).</summary>
    public int HubDegree { get; init; } = 80;
}

public sealed class ContextRetriever(GraphStore store, RetrieveOptions opt)
{
    // هرچه هزینه کمتر، رابطه مهم‌تر
    private static readonly Dictionary<string, double> Cost = new()
    {
        [EdgeKind.ResolvesTo]   = 0.25,
        [EdgeKind.Handles]      = 0.35,
        [EdgeKind.EndpointOf]   = 0.35,
        [EdgeKind.Overrides]    = 0.45,
        [EdgeKind.Implements]   = 0.50,
        [EdgeKind.Inherits]     = 0.60,
        [EdgeKind.Contains]     = 0.75,
        [EdgeKind.Calls]        = 1.00,
        [EdgeKind.Instantiates] = 1.00,
        [EdgeKind.Reads]        = 1.30,
        [EdgeKind.Writes]       = 1.30,
        [EdgeKind.UsesType]     = 1.45,
    };

    private const double IncomingPenalty = 1.25;  // فراخوان‌کننده کمی کم‌اهمیت‌تر از فراخوان‌شده

    public string BuildContext(string query)
    {
        var seeds = store.Search(query, 6);
        if (seeds.Count == 0) return $"// چیزی برای «{query}» پیدا نشد.";
        return BuildFromSeeds(seeds.Select(s => s.Id));
    }

    public string BuildFromSeeds(IEnumerable<string> seedIds)
    {
        var dist = new Dictionary<string, double>();
        var via  = new Dictionary<string, string>();
        var pq   = new PriorityQueue<string, double>();

        foreach (var s in seedIds)
        {
            if (store.GetNode(s) is null) continue;
            dist[s] = 0; via[s] = "seed";
            pq.Enqueue(s, 0);
        }

        var order = new List<string>();
        var done  = new HashSet<string>();

        while (pq.TryDequeue(out var cur, out var d) && order.Count < opt.MaxNodes)
        {
            if (!done.Add(cur)) continue;
            order.Add(cur);

            var node = store.GetNode(cur);
            if (node is null) continue;
            // از گره‌های پرتکرار عبور نکن، فقط نگهشان دار
            if (d > 0 && node.Degree > opt.HubDegree) continue;

            foreach (var nb in store.Neighbors(cur))
            {
                if (!Cost.TryGetValue(nb.Kind, out var c)) c = 1.5;
                if (nb.Dir == "in") c *= IncomingPenalty;

                var nd = d + c;
                if (nd > opt.MaxDistance) continue;
                if (dist.TryGetValue(nb.Id, out var old) && old <= nd) continue;

                dist[nb.Id] = nd;
                via[nb.Id]  = $"{(nb.Dir == "out" ? "→" : "←")} {nb.Kind}";
                pq.Enqueue(nb.Id, nd);
            }
        }

        return Render(order, dist, via);
    }

    private string Render(List<string> ids, Dictionary<string, double> dist, Dictionary<string, string> via)
    {
        var nodes = store.GetNodes(ids).ToDictionary(n => n.Id);
        var sb = new StringBuilder();
        int used = 0;

        sb.AppendLine("// ===== زیرگراف مرتبط با کد =====");

        foreach (var id in ids)
        {
            if (!nodes.TryGetValue(id, out var n)) continue;
            var d = dist[id];

            // بدنه‌ی کامل فقط برای seed و همسایه‌های نزدیک
            bool withBody = d <= 1.05 && n.Body is not null;
            var block = withBody
                ? $"\n// {n.File}:{n.Line}  [{via[id]}]\n{n.Body}\n"
                : $"// [{via[id]}] {n.Kind} {n.Signature}   ({Path.GetFileName(n.File)}:{n.Line})\n";

            var tokens = block.Length / 3;   // تقریب برای کد
            if (used + tokens > opt.TokenBudget)
            {
                if (withBody)
                {
                    block = $"// [{via[id]}] {n.Kind} {n.Signature}   ({Path.GetFileName(n.File)}:{n.Line})\n";
                    tokens = block.Length / 3;
                    if (used + tokens > opt.TokenBudget) break;
                }
                else break;
            }

            sb.Append(block);
            used += tokens;
        }

        sb.AppendLine($"\n// ===== {ids.Count} گره، حدود {used} توکن =====");
        return sb.ToString();
    }
}
