using Microsoft.Extensions.AI;

namespace Maf.Handoff.Core;

internal static class RepositoryToolFactory
{
    internal static IList<AITool> CreateReadTools(
        RepositoryWorkspace workspace,
        IReferenceDocumentSearch? referenceDocuments = null,
        bool compactFileReads = false)
    {
        List<AITool> tools =
        [
        AIFunctionFactory.Create(
            (Func<string?, string>)workspace.ListFiles,
            name: AgentToolCatalog.ListRepositoryFiles.Name),
        AIFunctionFactory.Create(
            compactFileReads
                ? (Func<string, string>)workspace.ReadFileCompact
                : workspace.ReadFile,
            name: AgentToolCatalog.ReadRepositoryFile.Name),
        AIFunctionFactory.Create(
            (Func<string, string?, string>)workspace.Search,
            name: AgentToolCatalog.SearchRepository.Name),
        AIFunctionFactory.Create(
            (Func<string>)workspace.GetStatus,
            name: AgentToolCatalog.GetRepositoryStatus.Name),
        AIFunctionFactory.Create(
            (Func<string>)workspace.GetDiff,
            name: AgentToolCatalog.GetRepositoryDiff.Name),
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
        .. CreateReadTools(workspace, referenceDocuments, compactFileReads: true),
        AIFunctionFactory.Create(
            (Func<string, string, string>)workspace.WriteFile,
            name: AgentToolCatalog.WriteRepositoryFile.Name),
        AIFunctionFactory.Create(
            (Func<string, string, string, string>)workspace.ReplaceText,
            name: AgentToolCatalog.ReplaceRepositoryText.Name),
    ];
}
