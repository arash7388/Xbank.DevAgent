using Xbank.DevAgent.Api;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Xbank.DevAgent.Tests;

public sealed class CodeGraphFreshnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maf-graph-freshness-" + Guid.NewGuid().ToString("N"));
    private readonly string _checkoutRoot = Path.Combine(Path.GetTempPath(), "maf-graph-checkout-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "sample", "Sample.csproj");
    private string Database => Path.Combine(_root, "graph.db");
    private CodeGraphService Service => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["CodeGraph:DatabasePath"] = Database,
    }).Build());

    public CodeGraphFreshnessTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        File.WriteAllText(Source, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(_root, "sample", "Service.cs"), "class Service { }");
    }

    [Fact]
    public void FingerprintIsIndependentOfTimestampsAndCheckoutLocation()
    {
        string initial = CodeGraphSourceFingerprint.Capture(Source).Hash;
        File.SetLastWriteTimeUtc(Source, DateTime.UtcNow.AddDays(-5));
        Assert.Equal(initial, CodeGraphSourceFingerprint.Capture(Source).Hash);
        string checkout = Path.Combine(_root, "other-checkout");
        Directory.CreateDirectory(Path.Combine(checkout, ".git"));
        Directory.CreateDirectory(Path.Combine(checkout, "sample"));
        File.Copy(Source, Path.Combine(checkout, "sample", "Sample.csproj"));
        File.Copy(Path.Combine(_root, "sample", "Service.cs"), Path.Combine(checkout, "sample", "Service.cs"));
        Assert.Equal(initial, CodeGraphSourceFingerprint.Capture(Path.Combine(checkout, "sample", "Sample.csproj")).Hash);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("rename")]
    [InlineData("project")]
    [InlineData("dependencies")]
    public void DetectsChangesThatCanAffectRelationships(string change)
    {
        string initial = CodeGraphSourceFingerprint.Capture(Source).Hash;
        string file = Path.Combine(_root, "sample", "Service.cs");
        switch (change)
        {
            case "edit": File.WriteAllText(file, "class Service { void Run() { } }"); break;
            case "add": File.WriteAllText(Path.Combine(_root, "sample", "New.cs"), "class New { }"); break;
            case "delete": File.Delete(file); break;
            case "rename": File.Move(file, Path.Combine(_root, "sample", "Renamed.cs")); break;
            case "project": File.AppendAllText(Source, "<!-- project reference changed -->"); break;
            case "dependencies": File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project />"); break;
        }
        Assert.NotEqual(initial, CodeGraphSourceFingerprint.Capture(Source).Hash);
    }

    [Fact]
    public void BuildOutputsAndUnrelatedDocumentationDoNotInvalidateFingerprint()
    {
        string initial = CodeGraphSourceFingerprint.Capture(Source).Hash;
        Directory.CreateDirectory(Path.Combine(_root, "sample", "obj"));
        File.WriteAllText(Path.Combine(_root, "sample", "obj", "generated.cs"), "class Generated { }");
        File.WriteAllText(Path.Combine(_root, ".git", "state.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "README.md"), "Documentation change");
        Assert.Equal(initial, CodeGraphSourceFingerprint.Capture(Source).Hash);
    }

    [Fact]
    public void OldDatabaseIsUnknownEvenWithRecentIndexedAt()
    {
        SeedDatabase(withFingerprint: false);
        CodeGraphFreshness result = Service.GetFreshness(Database, Source);
        Assert.Equal("unknown", result.State);
        Assert.False(result.IsFresh);
        Assert.Contains("Reindex", result.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndexingVerifiesAuditWarningsButRejectsProjectLoadFailures(bool auditOnly)
    {
        SeedDatabase();
        string indexerRoot = Path.Combine(_root, "indexer");
        string projectDirectory = Path.Combine(indexerRoot, "CodeGraph");
        Directory.CreateDirectory(projectDirectory);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "CodeGraph.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        string diagnostic = $"[msbuild] Msbuild failed when processing the file '{Source}' with message: " +
            (auditOnly
                ? "Package 'RestSharp' 106.11.3 has a known high severity vulnerability, https://github.com/advisories/GHSA-9pq7-rcxv-47vq"
                : "The reference assemblies for .NETFramework,Version=v4.8 were not found.");
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "Program.cs"),
            "System.Console.Error.WriteLine(" + System.Text.Json.JsonSerializer.Serialize(diagnostic) + ");");
        var service = new CodeGraphService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CodeGraph:RootPath"] = indexerRoot,
            ["CodeGraph:DatabasePath"] = Database,
        }).Build());

        service.StartIndex(new(Source, Database, Incremental: false, RestoreDependencies: false));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        while (service.GetIndexStatus().State == "running")
            await Task.Delay(100, deadline.Token);

        var status = service.GetIndexStatus();
        Assert.True(status.State == "completed_with_warnings", string.Join(Environment.NewLine, status.Log) + status.Error);
        Assert.Equal(auditOnly, Service.GetFreshness(Database, Source).IsFresh);
        Assert.Equal(auditOnly, status.Log.Any(line => line.StartsWith("Verified source fingerprint")));
        Assert.Contains(status.Log, line => line.Contains(auditOnly
            ? "[msbuild warning] Package security advisory" : "[msbuild] Msbuild failed"));
    }

    [Fact]
    public void PersistedFingerprintSurvivesServiceRestartAndBecomesStaleOnSourceChange()
    {
        SeedDatabase();
        CodeGraphFreshness fresh = Service.GetFreshness(Database, Source);
        Assert.True(fresh.IsFresh);
        Assert.Equal(fresh.IndexedHash, fresh.CurrentHash);
        Assert.Contains("indexed folder", fresh.Message);
        Assert.Contains("Runs verify their own checkout separately", fresh.Message);
        File.AppendAllText(Path.Combine(_root, "sample", "Service.cs"), "// changed");
        CodeGraphFreshness stale = Service.GetFreshness(Database, Source);
        Assert.Equal("stale", stale.State);
        Assert.Equal(fresh.IndexedHash, stale.IndexedHash);
        Assert.NotEqual(stale.IndexedHash, stale.CurrentHash);
    }

    [Fact]
    public void AnUnverifiedExternalIndexCannotReuseOldFingerprint()
    {
        SeedDatabase();
        using SqliteConnection connection = OpenDatabase();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE codebases SET indexed_at = 'another-index-operation'";
        command.ExecuteNonQuery();
        Assert.Equal("unknown", Service.GetFreshness(Database, Source).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompletedReadsReleaseWalLocksBeforeReindexing(bool checkFreshness)
    {
        SeedDatabase();
        using SqliteConnection indexer = OpenDatabase();
        using SqliteCommand command = indexer.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "PRAGMA journal_mode = WAL";
        Assert.Equal("wal", command.ExecuteScalar());

        if (checkFreshness)
            Assert.True(Service.GetFreshness(Database, Source).IsFresh);
        else
            Service.GetTable(Database, "codebases", 10, 0, null);

        // An indexer needs exclusive access when configuring its journal mode.
        // Disposed workbench reads must not leave pooled WAL connections open.
        command.CommandText = "PRAGMA journal_mode = DELETE";
        Assert.Equal("delete", command.ExecuteScalar());
    }

    [Fact]
    public void WorkflowChecksTheActualCheckoutInsteadOfTheOriginalSource()
    {
        SeedDatabase();
        // A separate checkout has matching relative paths but different source bytes.
        string checkout = _checkoutRoot;
        Directory.CreateDirectory(Path.Combine(checkout, ".git"));
        Directory.CreateDirectory(Path.Combine(checkout, "sample"));
        File.Copy(Source, Path.Combine(checkout, "sample", "Sample.csproj"));
        File.Copy(Path.Combine(_root, "sample", "Service.cs"), Path.Combine(checkout, "sample", "Service.cs"));
        const string repository = "https://example.test/Sample.git";
        var matching = Assert.Single(Service.CheckWorkflowFreshness(repository, checkout));
        Assert.True(matching.IsFresh);
        Assert.Contains(Path.Combine(checkout, "sample", "Sample.csproj"), matching.Message);
        File.WriteAllText(Path.Combine(checkout, "sample", "Service.cs"), "class Different { }");
        var stale = Assert.Single(Service.CheckWorkflowFreshness(repository, checkout));
        Assert.Equal("stale", stale.State);
        Assert.Contains(Path.Combine(checkout, "sample", "Sample.csproj"), stale.Message);
        Assert.Contains(Source, stale.Message);
        Assert.Contains("run's branch/revision", stale.Message);
        // The indexed folder still matches its baseline even when the run uses newer source.
        Assert.True(Service.GetFreshness(Database, Source).IsFresh);
    }

    [Fact]
    public void MissingDatabaseAndMissingCheckoutSourceProduceWarnings()
    {
        Assert.Equal("unknown", Assert.Single(Service.CheckWorkflowFreshness("https://example.test/Sample.git", _root)).State);
        SeedDatabase();
        File.Delete(Source);
        Assert.Equal("unavailable", Assert.Single(Service.CheckWorkflowFreshness("https://example.test/Sample.git", _root)).State);
    }

    [Fact]
    public void FingerprintingHonorsCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CodeGraphSourceFingerprint.Capture(Source, cancellation.Token));
    }

    private void SeedDatabase(bool withFingerprint = true)
    {
        CodeGraphSourceFingerprint fingerprint = CodeGraphSourceFingerprint.Capture(Source);
        using SqliteConnection connection = OpenDatabase();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE codebases (id TEXT PRIMARY KEY, name TEXT, source_path TEXT, indexed_at TEXT);
            CREATE TABLE nodes (id TEXT);
            CREATE TABLE edges (src TEXT);
            INSERT INTO codebases VALUES ($id, 'Sample', $source, 'successful-index');
            """;
        command.Parameters.AddWithValue("$id", Source.Replace('\\', '/'));
        command.Parameters.AddWithValue("$source", Source);
        command.ExecuteNonQuery();
        if (!withFingerprint) return;
        command.CommandText = """
            CREATE TABLE maf_source_fingerprints (
                source_path TEXT PRIMARY KEY, relative_source TEXT, source_hash TEXT, indexed_at TEXT);
            INSERT INTO maf_source_fingerprints VALUES ($id, $relative, $hash, 'successful-index');
            """;
        command.Parameters.AddWithValue("$relative", fingerprint.SourceRelativePath);
        command.Parameters.AddWithValue("$hash", fingerprint.Hash);
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_checkoutRoot)) Directory.Delete(_checkoutRoot, recursive: true);
    }
}
