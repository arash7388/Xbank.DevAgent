namespace XBank.CodeGraph.Model;

public sealed record NodeRow(
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

public sealed record Neighbor(
    string Id,
    string Kind,
    string Dir,      // "out" | "in"
    int Count);
