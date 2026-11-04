using System.Text.Json;
using Xbank.DevAgent.Api;
using Xbank.DevAgent.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Xbank.DevAgent.Tests;

public sealed class TaskAnalysisTests
{
    [Fact]
    public async Task AcceptsTokenValuesBeyondFormerAnalysisAndWorkflowCeilings()
    {
        var model = Model() with { Provider = Model().Provider with { MaxInputTokens = 5000000, MaxOutputTokens = 3000000 } };
        var service = new TaskAnalysisService(new Evidence(), new ConfigurationBuilder().Build());
        var client = new RecordingClient();
        var settings = model.ToSettings(10, 1, enforceTokenCeiling: false);
        settings.Validate();
        await service.GenerateAsync(new("TASK-1", model), settings, client);
        Assert.Equal(3000000, client.Options!.MaxOutputTokens);
        Assert.Throws<ConfigurationException>(() => model.ToSettings(10, 1));
        foreach (var provider in new[] { model.Provider with { MaxInputTokens = 0 }, model.Provider with { MaxOutputTokens = -1 } })
            Assert.Throws<ConfigurationException>(() => (model with { Provider = provider }).ToSettings(10, 1, enforceTokenCeiling: false));
    }

    [Fact]
    public void RejectsUnconfiguredRepositoryAndMissingLocalCheckout()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Projects:one:Key"] = "configured-repo",
        }).Build();
        var service = new TaskAnalysisService(new Evidence(), configuration);
        Assert.Null(service.OpenCodebase(null));
        Assert.Throws<ArgumentException>(() => service.OpenCodebase("unconfigured"));
        Assert.Throws<ArgumentException>(() => service.OpenCodebase("configured-repo"));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(20, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task FinishesExplanationAfterRepositoryInspection(int toolRounds, bool largeSource)
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-analysis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            async Task Git(params string[] args)
            {
                var start = new System.Diagnostics.ProcessStartInfo("git") {
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true,
                };
                foreach (string arg in args) start.ArgumentList.Add(arg);
                using var process = System.Diagnostics.Process.Start(start)!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, process.ExitCode);
            }
            await Git("init");
            await Git("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "--allow-empty", "-m", "Initial");
            string source = "// Current uncommitted behavior\npublic class Requirement {}" +
                (largeSource ? "\n// " + new string('x', 25000) + " UserActivityTail" : "");
            await File.WriteAllTextAsync(Path.Combine(root, "Requirement.cs"), source);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["Projects:one:Key"] = "selected", ["Projects:one:RepoPath"] = root,
            }).Build();
            var service = new TaskAnalysisService(new Evidence(), configuration);
            var workspace = service.OpenCodebase("selected")!;
            string before = workspace.GetStatus();
            var model = Model();
            var client = new RecordingClient { ReadSource = true, ToolRounds = toolRounds };
            var response = await service.GenerateAsync(new("TASK-1", model, ProjectKey: "selected"),
                model.ToSettings(10, 1), client, codebase: workspace);
            Assert.Equal("Requirement explanation", response.Text);
            Assert.DoesNotContain("I'll inspect", response.Text);
            Assert.DoesNotContain(client.Messages, message => message.Contents.OfType<FunctionCallContent>().Any());
            if (toolRounds > 0)
                Assert.Contains("1: // Current uncommitted behavior", string.Join('\n', client.Messages.Select(m => m.Text)));
            if (largeSource)
            {
                Assert.Contains("UserActivityTail", string.Join('\n', client.Messages.Select(m => m.Text)));
                Assert.DoesNotContain(response.Notices, notice => notice.Contains("partial") || notice.Contains("omitted"));
            }
            Assert.Contains("Selected codebase: selected", string.Join('\n', client.Messages.Select(m => m.Text)));
            Assert.Contains("Commit:", string.Join('\n', client.Messages.Select(m => m.Text)));
            Assert.Equal(new[] { "list_repository_files", "read_repository_file", "search_repository" },
                client.Calls[0].Options!.Tools!.Cast<AIFunction>().Select(tool => tool.Name));
            Assert.Null(client.Options!.Tools);
            Assert.Equal(ChatToolMode.None, client.Options.ToolMode);
            // The SDK may add a tool-disabled inspection turn after its eight tool rounds.
            Assert.InRange(client.Calls.Count, 2, 10);
            Assert.Equal(source, await File.ReadAllTextAsync(Path.Combine(root, "Requirement.cs")));
            Assert.Equal(before, workspace.GetStatus());
            if (toolRounds == 0)
                Assert.Contains(response.Notices, notice => notice.Contains("did not inspect"));
            else
                Assert.DoesNotContain(response.Notices, notice => notice.Contains("did not inspect"));
            if (toolRounds == 1 && !largeSource)
            {
                var reader = client.Calls[0].Options!.Tools!.OfType<AIFunction>().Single(tool => tool.Name == "read_repository_file");
                string firstPage = (await reader.InvokeAsync(new AIFunctionArguments { ["path"] = "Requirement.cs", ["maxLines"] = 1 }))!.ToString()!;
                Assert.Contains("startLine=2", firstPage);
                string secondPage = (await reader.InvokeAsync(new AIFunctionArguments { ["path"] = "Requirement.cs", ["startLine"] = 2, ["maxLines"] = 1 }))!.ToString()!;
                Assert.Contains("2: public class Requirement {}", secondPage);
                Assert.DoesNotContain("1: // Current", secondPage);
            }
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private static AgentModelConfiguration Model(int input = 128000) => new(
        new("analysis-model", "Analysis", "https://api.deepseek.com", true, false,
            input, 4096, true, "medium", ["low", "medium", "high"]), "test-key", "Focus on user behavior.");

    [Fact]
    public async Task LoadsPagedCommentsAndAttachmentsWithIndependentLanguageAndModelOptions()
    {
        var jira = new Evidence();
        var client = new RecordingClient();
        var model = Model();
        var service = new TaskAnalysisService(jira, new ConfigurationBuilder().Build());
        var result = await service.GenerateAsync(new("TASK-1", model, true, true, "Persian", true),
            model.ToSettings(10, 1), client);

        Assert.Equal(new[] { 0, 1 }, jira.CommentOffsets);
        Assert.Equal(new[] { 0, 5 }, jira.AttachmentOffsets);
        Assert.Equal("Persian", result.OutputLanguage);
        Assert.Contains("Write in Persian", client.Messages[0].Text);
        Assert.Contains("concrete illustrative example", client.Messages[0].Text);
        Assert.Contains("Focus on user behavior", client.Messages[0].Text);
        Assert.Contains("comment-two", client.Messages[1].Text);
        Assert.Contains("first", client.Messages[1].Text);
        Assert.Contains("last", client.Messages[1].Text);
        Assert.Contains("screenshot.png", Assert.Single(result.Notices));
        Assert.Null(client.Options!.Tools);
        Assert.Null(client.Options.Instructions);
        Assert.NotNull(client.Options.Reasoning);
        Assert.DoesNotContain("RUN BUILD POLICY", string.Join('\n', client.Messages.Select(m => m.Text)));
    }

    [Fact]
    public async Task ReportsOmittedAttachmentsWhenEvidenceExceedsInputAllowance()
    {
        var jira = new Evidence { LargeAttachment = true };
        var model = Model(10000);
        var service = new TaskAnalysisService(jira, new ConfigurationBuilder().Build());
        var client = new RecordingClient();
        var result = await service.GenerateAsync(new("TASK-1", model), model.ToSettings(10, 1), client);
        Assert.Contains(result.Notices, notice => notice.Contains("input allowance"));
        Assert.Contains("Evidence limitations", client.Messages[1].Text);
        Assert.Contains("Do not add an illustrative example", client.Messages[0].Text);
    }

    [Theory]
    [InlineData(false, false, "English", "TASK-1")]
    [InlineData(true, false, "French", "TASK-1")]
    [InlineData(true, false, "English", "invalid")]
    public async Task RejectsInvalidRequestsBeforeAccessingRemoteServices(bool analyse, bool summary, string language, string key)
    {
        var service = new TaskAnalysisService(new Evidence(), new ConfigurationBuilder().Build());
        await Assert.ThrowsAsync<ArgumentException>(() => service.AnalyseAsync(new(key, Model(), analyse, summary, language)));
    }

    [Fact]
    public async Task CancellationReachesEvidenceLoading()
    {
        var model = Model();
        var service = new TaskAnalysisService(new Evidence(), new ConfigurationBuilder().Build());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateAsync(
            new("TASK-1", model), model.ToSettings(10, 1), new RecordingClient(), cancel.Token));
    }

    private sealed class Evidence : IJiraIssueSearch
    {
        public List<int> CommentOffsets { get; } = [];
        public List<int> AttachmentOffsets { get; } = [];
        public bool LargeAttachment { get; init; }
        public Task<JsonElement> GetIssueDetailsAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(JsonSerializer.SerializeToElement(new { key, fields = new { summary = "Requirement", description = "Expected behavior" } }));
        }
        public Task<JsonElement> GetIssueCommentsAsync(string key, int startAt = 0, int maxResults = 50, CancellationToken cancellationToken = default)
        {
            CommentOffsets.Add(startAt);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { total = 2, comments = new[] { new { id = startAt, body = startAt == 0 ? "comment-one" : "comment-two" } } }));
        }
        public Task<IReadOnlyList<JiraAttachmentInfo>> GetAttachmentsAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JiraAttachmentInfo>>([new("1", "spec.txt", "text/plain", 9), new("2", "screenshot.png", "image/png", 100)]);
        public Task<JiraAttachmentText> ReadAttachmentAsync(string key, string id, int offset = 0, CancellationToken cancellationToken = default)
        {
            if (id == "2") throw new ArgumentException("Unsupported image format.");
            AttachmentOffsets.Add(offset);
            string text = LargeAttachment ? new string('x', 20000) : offset == 0 ? "first" : "last";
            return Task.FromResult(new JiraAttachmentText(key, id, "spec.txt", text, offset, 9, offset == 0, false));
        }
        public Task<IReadOnlyList<JiraIssueInfo>> SearchIssuesAsync(string jql, int maxResults = 10, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetIssueTitleByKeyAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingClient : IChatClient
    {
        public bool ReadSource { get; init; }
        public int ToolRounds { get; init; } = 1;
        private int _toolCalls;
        public List<(ChatMessage[] Messages, ChatOptions? Options)> Calls { get; } = [];
        public ChatMessage[] Messages { get; private set; } = [];
        public ChatOptions? Options { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Messages = messages.ToArray();
            Options = options;
            Calls.Add((Messages, options?.Clone()));
            if (ReadSource && options?.Tools?.Count > 0)
            {
                if (_toolCalls++ >= ToolRounds)
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        "I'll inspect the marketing codebase for the UserActivity implementation before explaining.")));
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new TextContent("I'll inspect the marketing codebase."),
                     new FunctionCallContent($"read-{_toolCalls}", "read_repository_file", new Dictionary<string, object?> { ["path"] = "Requirement.cs" })])));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Requirement explanation")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
