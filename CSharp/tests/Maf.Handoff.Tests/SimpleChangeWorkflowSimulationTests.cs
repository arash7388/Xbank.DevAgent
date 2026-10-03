using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Maf.Handoff.Core;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Maf.Handoff.Tests;

public sealed class SimpleChangeWorkflowSimulationTests
{
    [Fact]
    public async Task RepeatedReviewerReturnsStopAtRemediationLimitAsync()
    {
        ScriptedClient planner = new(options => Handoff(options, "plan"));
        ScriptedClient developer = new(options => Handoff(options, Guid.NewGuid().ToString()));
        ScriptedClient reviewer = new(options => Tool(Guid.NewGuid().ToString(),
            options!.Tools!.First(tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal)).Name,
            new()));
        ScriptedClient release = new(_ => throw new InvalidOperationException("Unresolved work must not be approved."));
        WorkflowSettings settings = new("https://example.test", "test-model", "test-key",
            MaxTotalMessages: 100, MaxAgentTurns: 30)
        {
            MaxReviewerRemediationCycles = 1,
        };
        Dictionary<string, IChatClient> clients = new()
        {
            [AgentNames.Planner] = planner,
            [AgentNames.Developer] = developer,
            [AgentNames.Reviewer] = reviewer,
            [AgentNames.ReleaseManager] = release,
        };
        WorkflowBundle bundle = HandoffWorkflowFactory.Create(clients,
            clients.Keys.ToDictionary(name => name, _ => settings));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
            bundle.Workflow, "Implement the change.", cancellationToken: timeout.Token);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        ChatMessage[] transcript = [];
        await foreach (WorkflowEvent workflowEvent in run.WatchStreamAsync(timeout.Token))
        {
            Assert.IsNotType<WorkflowErrorEvent>(workflowEvent);
            Assert.IsNotType<ExecutorFailedEvent>(workflowEvent);
            if (workflowEvent is WorkflowOutputEvent output)
            {
                transcript = [.. output.As<IEnumerable<ChatMessage>>() ?? []];
            }
        }

        Assert.Contains(transcript, message => HandoffTopology.IsReviewerRemediationLimitMessage(message.Text));
        Assert.Equal(2, reviewer.CallCount);
        Assert.Equal(2, developer.CallCount);
        Assert.Equal(0, release.CallCount);
    }

    [Fact]
    public async Task OneLineChangeUsesNineCallsAcrossFourRequiredAgentsAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-simple-change-simulation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string original = "namespace Simulation;\npublic static class Settings { public const int RetryLimit = 3; }\n";
            await File.WriteAllTextAsync(Path.Combine(root, "Settings.cs"), original);
            RunGit(root, "init");
            RunGit(root, "add", "Settings.cs");

            RepositoryWorkspace workspace = CreateWorkspace(root);
            ScriptedClient planner = new(options => Handoff(options, "planner-handoff"));
            int developerStep = 0;
            ScriptedClient developer = new(options => developerStep++ switch
            {
                0 => Tool("developer-search", AgentToolCatalog.SearchRepository.Name, new()
                {
                    ["query"] = "RetryLimit",
                    ["pathFilter"] = null,
                }),
                1 => Tool("developer-edit", AgentToolCatalog.ReplaceRepositoryText.Name, new()
                {
                    ["path"] = "Settings.cs",
                    ["oldText"] = "public const int RetryLimit = 3;",
                    ["newText"] = "public const int RetryLimit = 4;",
                }),
                2 => new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent("developer-status", AgentToolCatalog.GetRepositoryStatus.Name, new Dictionary<string, object?>()),
                    new FunctionCallContent("developer-diff", AgentToolCatalog.GetRepositoryDiff.Name, new Dictionary<string, object?>()),
                ])),
                _ => Handoff(options, "developer-handoff"),
            });
            int releaseStep = 0;
            int reviewerStep = 0;
            ScriptedClient reviewer = new(options =>
            {
                if (reviewerStep++ == 0)
                    return Tool("reviewer-diff", AgentToolCatalog.GetRepositoryDiff.Name, new());
                string handoff = options!.Tools!.Last(tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal)).Name;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [
                    new TextContent("REVIEW_APPROVED: inspected the RetryLimit change from 3 to 4."),
                    new FunctionCallContent("reviewer-handoff", handoff, new Dictionary<string, object?>()),
                ]));
            });
            ScriptedClient release = new(options => releaseStep++ == 0
                ? Tool("release-diff", AgentToolCatalog.GetRepositoryDiff.Name, new())
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "Ready. WORKFLOW_COMPLETE")));

            WorkflowSettings settings = new(
                "https://example.test",
                "simulated-model",
                "simulated-key",
                MaxTotalMessages: 30,
                MaxAgentTurns: 12);
            Dictionary<string, IChatClient> clients = new(StringComparer.Ordinal)
            {
                [AgentNames.Planner] = planner,
                [AgentNames.Developer] = developer,
                [AgentNames.Reviewer] = reviewer,
                [AgentNames.ReleaseManager] = release,
            };
            Dictionary<string, WorkflowSettings> settingsByAgent = clients.Keys.ToDictionary(
                name => name,
                _ => settings,
                StringComparer.Ordinal);
            WorkflowBundle bundle = HandoffWorkflowFactory.Create(clients, settingsByAgent, workspace);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

            await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
                bundle.Workflow,
                "Change RetryLimit from 3 to 4.",
                cancellationToken: timeout.Token);
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
            ChatMessage[] transcript = [];
            await foreach (WorkflowEvent workflowEvent in run.WatchStreamAsync(timeout.Token))
            {
                Assert.IsNotType<WorkflowErrorEvent>(workflowEvent);
                Assert.IsNotType<ExecutorFailedEvent>(workflowEvent);
                if (workflowEvent is WorkflowOutputEvent output)
                    transcript = [.. output.As<IEnumerable<ChatMessage>>() ?? []];
            }

            Assert.Equal(1, planner.CallCount);
            Assert.Equal(4, developer.CallCount);
            Assert.Equal(2, reviewer.CallCount);
            Assert.Equal(2, release.CallCount);
            Assert.Equal("completed", HandoffTopology.GetTerminationStatus(transcript, settings));
            Assert.Contains("RetryLimit = 4", workspace.GetDiff(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static ChatResponse Tool(
        string callId,
        string name,
        Dictionary<string, object?> arguments) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, name, arguments)]));

    private static ChatResponse Handoff(ChatOptions? options, string callId)
    {
        string handoff = options?.Tools?.Select(tool => tool.Name)
            .Single(name => name.StartsWith("handoff_to_", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The workflow did not provide one handoff tool.");
        return Tool(callId, handoff, new());
    }

    private static RepositoryWorkspace CreateWorkspace(string root)
    {
        ConstructorInfo constructor = typeof(RepositoryWorkspace).GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (RepositoryWorkspace)constructor.Invoke(
            [root, "https://github.com/example/simulation.git", "main", null]);
    }

    private static void RunGit(string root, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = root,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Git.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private static void DeleteDirectory(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    private sealed class ScriptedClient(Func<ChatOptions?, ChatResponse> next) : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(next(options));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Yield();
            foreach (ChatResponseUpdate update in next(options).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
