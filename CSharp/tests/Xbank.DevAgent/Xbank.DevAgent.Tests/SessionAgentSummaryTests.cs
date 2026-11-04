using System.Reflection;
using Xbank.DevAgent.Api;
using Xbank.DevAgent.Core;

namespace Xbank.DevAgent.Tests;

public sealed class SessionAgentSummaryTests
{
    [Fact]
    public void KeepsLatestSummaryThroughActivityTrimmingAndDatabaseReload()
    {
        string path = Path.Combine(Path.GetTempPath(), $"maf-agent-summaries-{Guid.NewGuid():N}.db");
        try
        {
            SqliteLlmCheckpointStore store = new(path);
            Guid id = Guid.NewGuid();
            store.Create(id, "repository_change", "{}", DateTimeOffset.UtcNow);
            CreateSessionRequest request = new("https://example.test/repo.git", "main", "Task 12345", new Dictionary<string, AgentModelConfiguration>(), null);
            Type stateType = typeof(RepositorySessionService).GetNestedType("SessionState", BindingFlags.NonPublic)!;
            object state = Activator.CreateInstance(stateType, BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [id, request, (Action<RunEvent>)(item => store.AppendEvent(id, item))], culture: null)!;
            MethodInfo addEvent = stateType.GetMethod("AddEvent", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void Add(RunEvent item) => addEvent.Invoke(state, [item]);

            Add(RunEvent.AgentSummary("planner", "Planned the edit."));
            Add(RunEvent.AgentSummary("developer", "Initial implementation."));
            Add(RunEvent.AgentSummary("reviewer", "This agent was not reached during the run.", "not_run"));
            Add(RunEvent.JiraProblem("WEPOD-42", "درخواست مبهم است.", "failed"));
            Add(RunEvent.JiraProblem("WEPOD-42", "نظر فارسی ثبت شد.", "posted", "https://jira.example.test/browse/WEPOD-42"));
            for (int i = 0; i < 510; i++) Add(RunEvent.Trace("developer", $"Activity {i}"));
            Add(RunEvent.AgentSummary("developer", "Fixed the review finding; tests passed."));

            SessionSnapshot snapshot = (SessionSnapshot)stateType.GetMethod("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, null)!;
            Assert.Equal(500, snapshot.Events.Count);
            Assert.Equal("posted", Assert.Single(snapshot.Events, item => item.Type == "jira_problem").Status);
            Assert.Equal("Planned the edit.", Assert.Single(snapshot.Events, item => item.Type == "agent_summary" && item.Agent == "planner").Text);
            Assert.Equal("Fixed the review finding; tests passed.", Assert.Single(snapshot.Events, item => item.Type == "agent_summary" && item.Agent == "developer").Text);

            IReadOnlyList<RunEvent> saved = new SqliteLlmCheckpointStore(path).GetLog(id).Events!;
            Assert.Equal("Fixed the review finding; tests passed.", saved.Last(item => item.Type == "agent_summary" && item.Agent == "developer").Text);
            Assert.Equal("not_run", saved.Last(item => item.Type == "agent_summary" && item.Agent == "reviewer").Status);
            Assert.Equal("نظر فارسی ثبت شد.", saved.Last(item => item.Type == "jira_problem").Text);
        }
        finally
        {
            foreach (string file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file)) File.Delete(file);
        }
    }
}
