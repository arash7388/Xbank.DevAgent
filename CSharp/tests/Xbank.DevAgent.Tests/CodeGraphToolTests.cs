using Xbank.DevAgent.Core;
using Microsoft.Extensions.AI;

namespace Xbank.DevAgent.Tests;

public sealed class CodeGraphToolTests
{
    [Fact]
    public async Task RecordsUsefulRelationshipEvidenceAsync()
    {
        FakeCodeGraphTools provider = new(contextResult: "# Context: OrderService");
        CodeGraphAudit audit = new();
        AIFunction function = GetContextFunction(provider, audit);

        await function.InvokeAsync(new AIFunctionArguments
        {
            ["query"] = "OrderService",
            ["maxNodes"] = 10,
            ["tokenBudget"] = 1_500,
        });

        CodeGraphToolEvidence evidence = Assert.Single(audit.Snapshot());
        Assert.Equal(AgentNames.Reviewer, evidence.Agent);
        Assert.Equal("OrderService", evidence.Subject);
    }

    [Fact]
    public async Task MissingRelationshipDataDoesNotCreateFalseEvidenceAsync()
    {
        FakeCodeGraphTools provider = new(contextResult: "No symbol matched 'MissingService'.");
        CodeGraphAudit audit = new();
        AIFunction function = GetContextFunction(provider, audit);

        object? result = await function.InvokeAsync(new AIFunctionArguments
        {
            ["query"] = "MissingService",
            ["maxNodes"] = 10,
            ["tokenBudget"] = 1_500,
        });

        Assert.Contains("No symbol", result?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(audit.Snapshot());
    }

    [Fact]
    public void GuidanceMakesMissingResultsNonBlocking()
    {
        string instructions = CodeGraphPolicy.InstructionsFor(AgentNames.Reviewer);

        Assert.Contains("never a handoff gate", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not retry", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Continue with targeted repository search", instructions, StringComparison.Ordinal);
    }

    private static AIFunction GetContextFunction(ICodeGraphTools provider, CodeGraphAudit audit) =>
        Assert.IsAssignableFrom<AIFunction>(CodeGraphToolFactory.Create(
            provider,
            audit,
            AgentNames.Reviewer).Single(tool => tool.Name == CodeGraphToolFactory.GetContextName));

    private sealed class FakeCodeGraphTools(string contextResult) : ICodeGraphTools
    {
        public void EnsureAvailable(string repositoryUrl)
        {
        }

        public string FindSymbol(string name, int limit = 15) => "[]";

        public string GetNeighbors(string id, int limit = 100) => "[]";

        public string GetContext(string query, int maxNodes = 15, int tokenBudget = 2_500) => contextResult;

        public string GetContextById(string id, int maxNodes = 15, int tokenBudget = 2_500) => contextResult;
    }
}
