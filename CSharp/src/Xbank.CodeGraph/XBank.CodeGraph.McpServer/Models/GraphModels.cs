using XBank.CodeGraph.Contracts;

namespace XBank.CodeGraph.McpServer.Models;

public sealed record ScoredNode(GraphNode Node, double Distance, string PathKind, int Depth);
