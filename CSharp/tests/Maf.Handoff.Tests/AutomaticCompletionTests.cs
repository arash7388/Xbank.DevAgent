using System.Diagnostics;
using System.Net;
using System.Reflection;
using Maf.Handoff.Api;
using Maf.Handoff.Core;
using Microsoft.Extensions.Configuration;

namespace Maf.Handoff.Tests;

public sealed class AutomaticCompletionTests
{
    [Theory]
    [InlineData("running")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("no_changes")]
    public async Task IncompleteOrUnsuccessfulRunsCannotTriggerPublishing(string status)
    {
        CreateSessionRequest request = new("https://github.com/example/repository", "main", "WEPOD-42",
            new Dictionary<string, AgentModelConfiguration>(), null,
            AutonomyLevel: 100, AutoCommit: true, AutoPush: true, AutoCreatePullRequest: true);
        Type stateType = typeof(RepositorySessionService).GetNestedType("SessionState", BindingFlags.NonPublic)!;
        object state = Activator.CreateInstance(stateType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [Guid.NewGuid(), request, (Action<RunEvent>)(_ => { })], null)!;
        stateType.GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(state, status);
        // No dependencies are provided: a publishing attempt would fail this test.
        RepositorySessionService service = new(null!, null!, null!, null!, null!, null!, null!, null!, null!);
        await (Task)typeof(RepositorySessionService).GetMethod("CompleteReviewedChangesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [state])!;
        SessionSnapshot snapshot = (SessionSnapshot)stateType.GetMethod("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, null)!;
        Assert.Equal(status, snapshot.Status);
        Assert.Null(snapshot.Approval);
        Assert.Null(snapshot.PullRequest);
    }

    [Theory]
    [InlineData(50, false, false, false, "awaiting_approval")]
    [InlineData(100, false, false, false, "completed")]
    [InlineData(100, true, false, false, "committed")]
    [InlineData(75, true, true, false, "pushed")]
    [InlineData(100, true, true, true, "pull_request_created")]
    public async Task OnlyAuthorizedCompletionActionsRun(int level, bool commit, bool push, bool pullRequest, string expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-autonomy-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(root, "repo");
        string remote = Path.Combine(root, "origin.git");
        Directory.CreateDirectory(repo);
        try
        {
            await Git(repo, "init", "--initial-branch=main");
            await Git(repo, "config", "commit.gpgSign", "false");
            await File.WriteAllTextAsync(Path.Combine(repo, "task.txt"), "original");
            await Git(repo, "add", ".");
            await Git(repo, "commit", "-m", "initial");
            await Git(root, "init", "--bare", remote);
            await Git(repo, "remote", "add", "origin", remote);
            await File.WriteAllTextAsync(Path.Combine(repo, "task.txt"), "changed");
            RepositoryWorkspace workspace = (RepositoryWorkspace)Activator.CreateInstance(typeof(RepositoryWorkspace),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [repo, "https://github.com/example/repository", "main", "local-test-token"], null)!;
            SqliteLlmCheckpointStore store = new(Path.Combine(root, "runs.db"));
            using Requests handler = new();
            using HttpClient http = new(handler) { BaseAddress = new("https://api.github.com/") };
            RepositorySessionService service = new(new(http), new(http), null!, null!, null!, null!, store, store,
                new ConfigurationBuilder().Build());
            CreateSessionRequest request = new(workspace.RepositoryUrl, "main", "WEPOD-42 update behavior",
                new Dictionary<string, AgentModelConfiguration>(), "local-test-token", SourceControl: "github",
                AutonomyLevel: level, AutoCommit: commit, AutoPush: push, AutoCreatePullRequest: pullRequest);
            Guid id = Guid.NewGuid();
            store.Create(id, "repository_change", "{}", DateTimeOffset.UtcNow);
            Type stateType = typeof(RepositorySessionService).GetNestedType("SessionState", BindingFlags.NonPublic)!;
            object state = Activator.CreateInstance(stateType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                [id, request, (Action<RunEvent>)(item => store.AppendEvent(id, item))], null)!;
            void Invoke(string name, params object[] args) => stateType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, args);
            object sessions = typeof(RepositorySessionService).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            sessions.GetType().GetMethod("TryAdd")!.Invoke(sessions, [id, state]);
            Invoke("BeginAgentRun", workspace);
            Invoke("ReadyForApproval", workspace.GetDiff());
            await (Task)typeof(RepositorySessionService).GetMethod("CompleteReviewedChangesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [state])!;
            Invoke("FinishAutomaticCompletion");
            SessionSnapshot snapshot = service.Get(id);
            Assert.Equal(expected, snapshot.Status);
            Assert.Equal(commit, snapshot.Approval is not null);
            Assert.Equal(pullRequest ? 1 : 0, handler.Calls);
            Assert.Equal(commit ? "user/WEPOD-42" : "main", await Git(repo, "branch", "--show-current"));
            Assert.Equal(commit ? "" : "M task.txt", await Git(repo, "status", "--porcelain"));
            Assert.Equal(push ? "refs/heads/user/WEPOD-42" : "", await Git(root, "--git-dir=" + remote, "for-each-ref", "--format=%(refname)", "refs/heads/"));
            if (push) Assert.True(snapshot.Approval!.Pushed);
            if (pullRequest) Assert.Equal(42, snapshot.PullRequest!.Number);
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task InvalidCompletionDependenciesAreRejected(bool commit, bool push, bool request)
    {
        CreateSessionRequest input = new("https://github.com/example/repository", "main", "WEPOD-42",
            new Dictionary<string, AgentModelConfiguration>(), null,
            AutoCommit: commit, AutoPush: push, AutoCreatePullRequest: request);
        RepositorySessionService service = new(null!, null!, null!, null!, null!, null!, null!, null!,
            new ConfigurationBuilder().Build());
        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(input));
        Assert.Contains("requires", error.Message);
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        ProcessStartInfo info = RepositoryWorkspace.CreateGitStartInfo(directory,
            ["-c", "user.name=MAF Tests", "-c", "user.email=maf-tests@example.com", .. arguments], RepositoryProvider.GitHub, null);
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return (await output).Trim();
    }

    private sealed class Requests : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/repos/example/repository/pulls", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"number\":42,\"html_url\":\"https://github.com/example/repository/pull/42\"}"),
            });
        }
    }
}
