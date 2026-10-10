using System.Reflection;
using System.Text;
using Maf.Handoff.Core;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class RepositoryTextEditingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maf-text-editing", Guid.NewGuid().ToString("N"));

    public RepositoryTextEditingTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("\r\n", false)]
    [InlineData("\r\n", true)]
    [InlineData("\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r", false)]
    [InlineData("\r", true)]
    public async Task DeveloperCanRemoveClaimWithLfPayloadAndPreserveSourceBytes(string lineEnding, bool bom)
    {
        const string original = "using XBank.OAuth.Filters;\n\n\n// keep trailing spaces  \n" +
            "        [Authorize(AuthenticationSchemes = XBankAuthenticationScheme.Signature)]\n" +
            "        [ClaimRequirement(\"MCP_HandleTools\")]\n" +
            "        public Task<CallToolDto> CallToolAsync() => Execute();\n";
        string source = original.Replace("\n", lineEnding, StringComparison.Ordinal);
        Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom);
        string path = Path.Combine(_root, "ToolsController.cs");
        File.WriteAllText(path, source, encoding);
        RepositoryWorkspace workspace = CreateWorkspace();
        IList<AITool> tools = RepositoryToolFactory.CreateDeveloperTools(workspace);
        AIFunction read = Assert.IsAssignableFrom<AIFunction>(Assert.Single(tools,
            tool => tool.Name == AgentToolCatalog.ReadRepositoryFile.Name));
        Assert.Equal(source, (await read.InvokeAsync(new AIFunctionArguments { ["path"] = "ToolsController.cs" }))?.ToString());

        AIFunction edit = Assert.IsAssignableFrom<AIFunction>(Assert.Single(tools,
            tool => tool.Name == AgentToolCatalog.ReplaceRepositoryText.Name));
        object? result = await edit.InvokeAsync(new AIFunctionArguments
        {
            ["path"] = "ToolsController.cs",
            ["oldText"] = "        [ClaimRequirement(\"MCP_HandleTools\")]\n        public Task<CallToolDto> CallToolAsync",
            ["newText"] = "        public Task<CallToolDto> CallToolAsync",
        });
        Assert.Contains("Replaced one exact occurrence", result?.ToString());
        Assert.Contains("Replaced one exact occurrence", workspace.ReplaceText("ToolsController.cs", "using XBank.OAuth.Filters;\n", ""));

        string expected = source.Replace("        [ClaimRequirement(\"MCP_HandleTools\")]" + lineEnding, "", StringComparison.Ordinal)
            .Replace("using XBank.OAuth.Filters;" + lineEnding, "", StringComparison.Ordinal);
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(expected)).ToArray(), File.ReadAllBytes(path));
        Assert.Contains("XBankAuthenticationScheme.Signature", workspace.ReadFile("ToolsController.cs"));
    }

    [Fact]
    public void ReplacementUsesMatchedLineEndingAndLeavesMixedSurroundingsUntouched()
    {
        const string source = "prefix\nfirst\r\nsecond\r\nsuffix\r";
        string path = Path.Combine(_root, "example.txt");
        File.WriteAllText(path, source);
        Assert.Contains("Replaced one exact occurrence", CreateWorkspace().ReplaceText(
            "example.txt", "first\nsecond", "updated\nline"));
        Assert.Equal("prefix\nupdated\r\nline\r\nsuffix\r", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("first\r\nsecond\nfirst\nsecond", "first\nsecond", "more than once")]
    [InlineData("first  \r\nsecond", "first\nsecond", "not found")]
    [InlineData("first\r\nsecond", "First\nsecond", "not found")]
    public void AmbiguousOrNonExactEditsNeverChangeTheFile(string source, string oldText, string reason)
    {
        string path = Path.Combine(_root, "example.txt");
        File.WriteAllText(path, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        byte[] original = File.ReadAllBytes(path);
        Assert.Contains(reason, CreateWorkspace().ReplaceText("example.txt", oldText, "replacement"));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    private RepositoryWorkspace CreateWorkspace() => (RepositoryWorkspace)Activator.CreateInstance(
        typeof(RepositoryWorkspace), BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null, args: [_root, "https://github.com/example/repository", "main", null], culture: null)!;

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
