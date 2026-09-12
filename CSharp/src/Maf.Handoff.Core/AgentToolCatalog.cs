namespace Maf.Handoff.Core;

public sealed record AgentToolDefinition(
    string Name,
    string Description,
    string Availability = AgentToolCatalog.Always);

public static class AgentToolCatalog
{
    public const string Always = "always";
    public const string WithReferenceDocuments = "with_reference_documents";
    public const string WithCSharpCodeGraph = CodeGraphPolicy.WithCSharpCodeGraph;

    public static AgentToolDefinition ListRepositoryFiles { get; } = new(
        "list_repository_files",
        "List files in the cloned repository.");

    public static AgentToolDefinition ReadRepositoryFile { get; } = new(
        "read_repository_file",
        "Read a text file from the cloned repository.");

    public static AgentToolDefinition SearchRepository { get; } = new(
        "search_repository",
        "Search repository file names and contents.");

    public static AgentToolDefinition GetRepositoryStatus { get; } = new(
        "get_repository_status",
        "Inspect the repository working-tree status.");

    public static AgentToolDefinition GetRepositoryDiff { get; } = new(
        "get_repository_diff",
        "Inspect the current repository diff.");

    public static AgentToolDefinition WriteRepositoryFile { get; } = new(
        "write_repository_file",
        "Create or replace a text file in the cloned repository.");

    public static AgentToolDefinition ReplaceRepositoryText { get; } = new(
        "replace_repository_text",
        "Replace an exact text occurrence in a repository file.");

    public static AgentToolDefinition SearchDocuments { get; } = new(
        "search_documents",
        "Search reference documents attached to the run.",
        WithReferenceDocuments);

    public static AgentToolDefinition FindSymbol { get; } = new(
        CodeGraphToolFactory.FindSymbolName,
        "Find an exact or partial internal C# symbol in the protected CodeGraph index.",
        WithCSharpCodeGraph);

    public static AgentToolDefinition GetNeighbors { get; } = new(
        CodeGraphToolFactory.GetNeighborsName,
        "Inspect direct incoming and outgoing relationships for an exact C# symbol ID.",
        WithCSharpCodeGraph);

    public static AgentToolDefinition GetContext { get; } = new(
        CodeGraphToolFactory.GetContextName,
        "Retrieve compact source and relationship context for a C# symbol.",
        WithCSharpCodeGraph);

    public static AgentToolDefinition GetContextById { get; } = new(
        CodeGraphToolFactory.GetContextByIdName,
        "Retrieve compact source and relationship context for an exact C# symbol ID.",
        WithCSharpCodeGraph);

    private static readonly IReadOnlyList<AgentToolDefinition> ReadTools =
    [
        ListRepositoryFiles,
        ReadRepositoryFile,
        SearchRepository,
        GetRepositoryStatus,
        GetRepositoryDiff,
        SearchDocuments,
    ];

    private static readonly IReadOnlyList<AgentToolDefinition> DeveloperTools =
    [
        .. ReadTools,
        WriteRepositoryFile,
        ReplaceRepositoryText,
    ];

    private static readonly IReadOnlyList<AgentToolDefinition> CodeGraphTools =
    [
        FindSymbol,
        GetNeighbors,
        GetContext,
        GetContextById,
    ];

    public static IReadOnlyDictionary<string, IReadOnlyList<AgentToolDefinition>> ByAgent { get; } =
        AgentNames.All.ToDictionary(
            name => name,
            name => name switch
            {
                AgentNames.Jira => (IReadOnlyList<AgentToolDefinition>)[],
                AgentNames.Developer => [.. DeveloperTools, .. CodeGraphTools],
                AgentNames.Planner or AgentNames.Reviewer or AgentNames.Security =>
                    [.. ReadTools, .. CodeGraphTools],
                _ => ReadTools,
            });
}
