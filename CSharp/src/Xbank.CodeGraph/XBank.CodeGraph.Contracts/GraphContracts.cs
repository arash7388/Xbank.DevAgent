namespace XBank.CodeGraph.Contracts;

public static class NodeKind
{
    public const string Class = "Class";
    public const string Interface = "Interface";
    public const string Struct = "Struct";
    public const string Record = "Record";
    public const string Enum = "Enum";
    public const string Method = "Method";
    public const string Constructor = "Constructor";
    public const string Property = "Property";
    public const string Field = "Field";
    public const string Event = "Event";
    public const string Endpoint = "Endpoint";
}

public static class EdgeKind
{
    public const string Calls = "CALLS";
    public const string Instantiates = "INSTANTIATES";
    public const string Inherits = "INHERITS";
    public const string Implements = "IMPLEMENTS";
    public const string Overrides = "OVERRIDES";
    public const string Contains = "CONTAINS";
    public const string UsesType = "USES_TYPE";
    public const string Reads = "READS";
    public const string Writes = "WRITES";
    public const string ResolvesTo = "RESOLVES_TO";
    public const string Handles = "HANDLES";
    public const string EndpointOf = "ENDPOINT_OF";
}

public sealed record GraphNode(
    string Id,
    string Kind,
    string Name,
    string FullName,
    string? File,
    int Line,
    string Signature,
    string? Body,
    bool IsExternal,
    int Degree);

public sealed record GraphEdge(string SourceId, string TargetId, string Kind, int EvidenceCount);
