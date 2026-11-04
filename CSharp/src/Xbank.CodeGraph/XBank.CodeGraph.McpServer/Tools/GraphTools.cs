using System.ComponentModel;
using System.Text.Json;
using XBank.CodeGraph.McpServer.Retrieval;
using ModelContextProtocol.Server;

namespace XBank.CodeGraph.McpServer.Tools;

[McpServerToolType]
public sealed class GraphTools
{
    [McpServerTool(Name = "get_context")]
    [Description("Returns relevant code-graph context for a C# symbol, including calls, callers, implementations, inheritance, and dependency-injection relationships. Use this to understand a symbol and its surrounding code without reading every related file.")]
    public static string GetContext(
        ContextRetriever retriever,
        [Description("Class, method, interface, or other symbol name. Examples: OrderService or ProcessPayment.")]
        string query,
        [Description("Maximum number of graph nodes to return. Allowed range: 1-100. Default: 25.")]
        int maxNodes = 25,
        [Description("Approximate token budget for the returned context. Allowed range: 250-20000. Default: 5000.")]
        int tokenBudget = 5000)
        => retriever.Retrieve(query, Math.Clamp(maxNodes, 1, 100), Math.Clamp(tokenBudget, 250, 20_000));

    [McpServerTool(Name = "find_symbol")]
    [Description("Quickly searches for internal C# symbols by partial or exact name. Returns symbol IDs, kinds, signatures, and source locations. Use this first when you need an exact symbol ID or when a name may be ambiguous.")]
    public static string FindSymbol(
        GraphStore store,
        [Description("Partial or exact symbol name to search for.")] string name,
        [Description("Maximum number of matches to return. Allowed range: 1-100. Default: 15.")] int limit = 15)
    {
        var nodes = store.FindNodes(name, Math.Clamp(limit, 1, 100));

        return JsonSerializer.Serialize(nodes.Select(n => new
        {
            n.Id,
            n.Name,
            n.Kind,
            n.FullName,
            File = n.File,
            n.Line,
            n.Signature,
            n.Degree
        }));
    }

    [McpServerTool(Name = "get_neighbors")]
    [Description("Returns direct incoming and outgoing code-graph relationships for an exact symbol ID. Use this to inspect callers, callees, implementations, inheritance, containment, and other immediate connections.")]
    public static string GetNeighbors(
        GraphStore store,
        [Description("Exact symbol ID returned by find_symbol.")] string id,
        [Description("Maximum number of relationships to return. Allowed range: 1-250. Default: 100.")] int limit = 100)
    {
        var relations = store.GetNeighbors(id, Math.Clamp(limit, 1, 250))
            .Select(item => new
            {
                Direction = item.Outgoing ? "outgoing" : "incoming",
                item.Edge.Kind,
                item.Edge.EvidenceCount,
                Node = new
                {
                    item.Node.Id,
                    item.Node.Name,
                    item.Node.Kind,
                    item.Node.FullName,
                    item.Node.File,
                    item.Node.Line,
                    item.Node.Signature
                }
            });

        return JsonSerializer.Serialize(relations);
    }

    [McpServerTool(Name = "get_context_by_id")]
    [Description("Returns relevant code-graph context for an exact symbol ID, including source and related symbols. Use this after find_symbol when a name is ambiguous or precise symbol selection is important.")]
    public static string GetContextById(
        ContextRetriever retriever,
        [Description("Exact symbol ID returned by find_symbol.")] string id,
        [Description("Maximum number of graph nodes to return. Allowed range: 1-100. Default: 25.")] int maxNodes = 25,
        [Description("Approximate token budget for the returned context. Allowed range: 250-20000. Default: 5000.")] int tokenBudget = 5000)
        => retriever.RetrieveById(id, Math.Clamp(maxNodes, 1, 100), Math.Clamp(tokenBudget, 250, 20_000));
}
