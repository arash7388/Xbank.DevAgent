using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

internal static class RepositoryToolFactory
{
    internal static IList<AITool> CreateReadTools(
        RepositoryWorkspace workspace,
        IReferenceDocumentSearch? referenceDocuments = null)
    {
        List<AITool> tools =
        [
        AIFunctionFactory.Create(
            (Func<string?, string>)workspace.ListFiles,
            name: "list_repository_files"),
        AIFunctionFactory.Create(
            (Func<string, string>)workspace.ReadFile,
            name: "read_repository_file"),
        AIFunctionFactory.Create(
            (Func<string, string?, string>)workspace.Search,
            name: "search_repository"),
        AIFunctionFactory.Create(
            (Func<string>)workspace.GetStatus,
            name: "get_repository_status"),
        AIFunctionFactory.Create(
            (Func<string>)workspace.GetDiff,
            name: "get_repository_diff"),
        ];
        if (referenceDocuments is not null)
        {
            tools.Add(ReferenceDocumentToolFactory.Create(referenceDocuments));
        }

        return tools;
    }

    internal static IList<AITool> CreateDeveloperTools(
        RepositoryWorkspace workspace,
        IReferenceDocumentSearch? referenceDocuments = null) =>
    [
        .. CreateReadTools(workspace, referenceDocuments),
        AIFunctionFactory.Create(
            (Func<string, string, string>)workspace.WriteFile,
            name: "write_repository_file"),
        AIFunctionFactory.Create(
            (Func<string, string, string, string>)workspace.ReplaceText,
            name: "replace_repository_text"),
    ];
}
