using XBank.CodeGraph.Model;
using XBank.CodeGraph.McpServer.Retrieval;
using Microsoft.Data.Sqlite;
using WriterStore = XBank.CodeGraph.Storage.GraphStore;
using Xunit;

namespace XBank.CodeGraph.Tests;

public sealed class GraphContractTests
{
    [Fact]
    public void FinalizationCountsDirectionsKindsAndSelfLoopsWithoutCountingDuplicateEvidence()
    {
        var db = Path.Combine(Path.GetTempPath(), $"codegraph-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new WriterStore(db);
            // UpsertEdge skips self-loops, but finalization must preserve the
            // degree semantics of loops already present in an existing database.
            using (var seed = new SqliteConnection($"Data Source={db}"))
            {
                seed.Open();
                using var command = seed.CreateCommand();
                command.CommandText = "INSERT INTO edge_evidence (src, dst, kind, codebase_id, file, count) VALUES ('A', 'A', 'CALLS', 'legacy', 'source.cs', 1)";
                command.ExecuteNonQuery();
            }
            store.BeginBatch();
            foreach (var id in new[] { "A", "B", "Isolated" })
                store.UpsertNode(new NodeRow(id, "Class", id, id, "source.cs", 1, id, null, false, 99));
            store.UpsertEdge("A", "B", "CALLS", "source.cs");
            store.UpsertEdge("A", "B", "CALLS", "source.cs");
            store.UpsertEdge("A", "B", "USES_TYPE", "source.cs");
            store.UpsertEdge("B", "A", "CALLS", "source.cs");
            store.Finalize_();
            store.CommitBatch();

            Assert.Equal(5, store.GetNode("A")!.Degree);
            Assert.Equal(3, store.GetNode("B")!.Degree);
            Assert.Equal(0, store.GetNode("Isolated")!.Degree);

            // Repeated finalization must replace previous degrees rather than accumulate them.
            store.BeginBatch();
            store.Finalize_();
            store.CommitBatch();
            Assert.Equal(5, store.GetNode("A")!.Degree);
            Assert.Equal(3, store.GetNode("B")!.Degree);
            Assert.Equal(0, store.GetNode("Isolated")!.Degree);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { db, $"{db}-wal", $"{db}-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void PurgingOneCodebaseDoesNotRemoveAnotherCodebasesEvidence()
    {
        var db = Path.Combine(Path.GetTempPath(), $"codegraph-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new WriterStore(db);
            foreach (var codebase in new[] { "repo-a", "repo-b" })
            {
                store.UpsertCodebase(codebase, codebase, $"/{codebase}/app.sln");
                store.BeginBatch(codebaseId: codebase);
                store.UpsertNode(new NodeRow("A|T:Shared", "Class", "Shared", "Shared", "shared.cs", 1, "Shared", null, false, 0));
                store.UpsertNode(new NodeRow("A|M:Target.Run", "Method", "Run", "Target.Run", "target.cs", 1, "Run", null, false, 0));
                store.UpsertEdge("A|T:Shared", "A|M:Target.Run", "CALLS", "shared.cs");
                store.Finalize_();
                store.CommitBatch();
                store.SetFileHashes(codebase, new Dictionary<string, string> { ["shared.cs"] = codebase });
            }

            Assert.Equal(2, Assert.Single(new XBank.CodeGraph.McpServer.Retrieval.GraphStore(db).GetNeighbors("A|T:Shared")).Edge.EvidenceCount);

            store.BeginBatch(codebaseId: "repo-a");
            store.PurgeFiles(["shared.cs", "target.cs"]);
            store.Finalize_();
            store.CommitBatch();

            Assert.Equal(1, Assert.Single(new XBank.CodeGraph.McpServer.Retrieval.GraphStore(db).GetNeighbors("A|T:Shared")).Edge.EvidenceCount);
            Assert.Single(store.GetFileHashes("repo-a"));
            Assert.Single(store.GetFileHashes("repo-b"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { db, $"{db}-wal", $"{db}-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void McpReadsIndexerSchemaAndFilePurgePreservesOtherEvidence()
    {
        var db = Path.Combine(Path.GetTempPath(), $"codegraph-{Guid.NewGuid():N}.db");
        try
        {
            using (var writer = new WriterStore(db))
            {
                WriteGraph(writer);

                var reader = new XBank.CodeGraph.McpServer.Retrieval.GraphStore(db);
                var match = Assert.Single(reader.FindNodes("Service"));
                Assert.Equal("AssemblyA|T:Sample.Service", match.Id);

                var first = Assert.Single(reader.GetNeighbors(match.Id));
                Assert.True(first.Outgoing);
                Assert.Equal(3, first.Edge.EvidenceCount);

                var context = new ContextRetriever(reader).Retrieve("Service", maxNodes: 10, tokenBudget: 500);
                Assert.Contains("Sample.Service", context);
                Assert.Contains("Calls", context);
                Assert.Contains("Sample.Service", new ContextRetriever(reader).RetrieveById(match.Id, maxNodes: 10, tokenBudget: 500));

                writer.BeginBatch();
                writer.PurgeFiles(["first.cs"]);
                writer.Finalize_();
                writer.CommitBatch();

                var remaining = Assert.Single(reader.GetNeighbors(match.Id));
                Assert.Equal(1, remaining.Edge.EvidenceCount);

                WriteGraph(writer);
                var rebuilt = Assert.Single(reader.GetNeighbors(match.Id));
                Assert.Equal(3, rebuilt.Edge.EvidenceCount);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { db, $"{db}-wal", $"{db}-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void WriteGraph(WriterStore store)
    {
        store.BeginBatch(resetGraph: true);
        store.UpsertNode(new NodeRow(
            "AssemblyA|T:Sample.Service", "Class", "Service", "Sample.Service",
            "first.cs", 1, "Sample.Service", null, false, 0));
        store.UpsertNode(new NodeRow(
            "AssemblyA|T:Sample.Service", "Class", "Service", "Sample.Service",
            "second.cs", 3, "Sample.Service", null, false, 0));
        store.UpsertNode(new NodeRow(
            "AssemblyA|M:Sample.Dependency.Run", "Method", "Run", "Sample.Dependency.Run",
            "dependency.cs", 2, "void Sample.Dependency.Run()", null, false, 0));
        store.UpsertEdge("AssemblyA|T:Sample.Service", "AssemblyA|M:Sample.Dependency.Run", "CALLS", "first.cs");
        store.UpsertEdge("AssemblyA|T:Sample.Service", "AssemblyA|M:Sample.Dependency.Run", "CALLS", "first.cs");
        store.UpsertEdge("AssemblyA|T:Sample.Service", "AssemblyA|M:Sample.Dependency.Run", "CALLS", "second.cs");
        store.Finalize_();
        store.CommitBatch();
    }
}
