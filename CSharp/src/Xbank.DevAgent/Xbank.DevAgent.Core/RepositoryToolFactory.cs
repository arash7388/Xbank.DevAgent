using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Core;

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

        return [.. tools.Select(tool => tool is AIFunction function
            ? (AITool)new RecoverableRepositoryAIFunction(function) : tool)];
    }

    internal static IList<AITool> CreateDeveloperTools(
        RepositoryWorkspace workspace,
        IReferenceDocumentSearch? referenceDocuments = null) =>
    [
        .. CreateReadTools(workspace, referenceDocuments),
        new RecoverableRepositoryAIFunction(AIFunctionFactory.Create(
            (Func<string, string, string>)workspace.WriteFile,
            name: AgentToolCatalog.WriteRepositoryFile.Name)),
        new RecoverableRepositoryAIFunction(AIFunctionFactory.Create(
            (Func<string, string, string, string>)workspace.ReplaceText,
            name: AgentToolCatalog.ReplaceRepositoryText.Name)),
    ];
}

internal sealed class RecoverableRepositoryAIFunction(AIFunction inner) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (RepositoryException error)
        {
            return $"Repository tool request failed: {error.Message} " +
                "Use list_repository_files to discover supported source files and pass an exact repository-relative path. " +
                "Absolute paths, paths outside the clone, .git files, symbolic links and unsupported file types are unavailable.";
        }
    }
}
