using Microsoft.Data.Sqlite;

namespace Maf.Handoff.Api;

public sealed record CodeGraphFreshness(
    string SourcePath, string State, string? CurrentHash, string? IndexedHash, string Message)
{
    public bool IsFresh => State == "fresh";
}

public sealed partial class CodeGraphService
{
    public CodeGraphFreshness GetFreshness(string databasePath, string sourcePath,
        CancellationToken cancellationToken = default)
    {
        string source = RequireExistingSource(sourcePath);
        using SqliteConnection connection = Open(databasePath);
        return CheckFreshness(connection, source, null, cancellationToken);
    }

    public IReadOnlyList<CodeGraphFreshness> CheckWorkflowFreshness(string repositoryUrl, string checkoutRoot,
        CancellationToken cancellationToken = default)
    {
        try
        {
            lock (_statusLock)
                if (_status.State == "running")
                    return [new("", "unknown", null, null, "CodeGraph indexing is running; freshness cannot be verified yet.")];
            using SqliteConnection connection = Open(_agentDatabasePath);
            string name = GetRepositoryName(repositoryUrl);
            var codebases = QueryRows(connection, "SELECT source_path, name FROM codebases")
                .Where(row => string.Equals(row["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase) ||
                    PathContainsRepositoryName(row["source_path"]?.ToString(), name)).ToList();
            if (codebases.Count == 0)
                return [new("", "unknown", null, null, $"CodeGraph has no verified index for repository '{name}'.")];
            return codebases.Select(row => CheckFreshness(connection, (string)row["source_path"]!,
                checkoutRoot, cancellationToken)).ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or SqliteException)
        {
            return [new("", "unknown", null, null, $"CodeGraph freshness could not be verified: {exception.Message}")];
        }
    }

    private static CodeGraphFreshness CheckFreshness(SqliteConnection connection, string source,
        string? checkoutRoot, CancellationToken cancellationToken = default)
    {
        string key = SourceKey(source);
        var baseline = GetAvailableTables(connection).Contains("maf_source_fingerprints")
            ? ReadBaseline(connection, key) : null;
        if (baseline is null)
            return new(source, "unknown", null, null,
                $"CodeGraph freshness is unknown for '{source}'. Reindex it in the CodeGraph workbench to establish a source fingerprint.");
        string indexedHash = (string)baseline["source_hash"]!;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT indexed_at FROM codebases WHERE id = $id";
        command.Parameters.AddWithValue("$id", key);
        if (!string.Equals(command.ExecuteScalar()?.ToString(), baseline["indexed_at"]?.ToString(), StringComparison.Ordinal))
            return new(source, "unknown", null, indexedHash,
                "CodeGraph was indexed outside the verified indexing operation. Reindex in the workbench to verify freshness.");
        try
        {
            string currentSource = source;
            if (checkoutRoot is not null)
            {
                string root = Path.GetFullPath(checkoutRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                currentSource = Path.GetFullPath(Path.Combine(root, (string)baseline["relative_source"]!));
                if (!currentSource.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The indexed source path is outside the current checkout.");
            }
            var current = CodeGraphSourceFingerprint.Capture(currentSource, cancellationToken);
            bool fresh = current.Hash == indexedHash;
            return new(source, fresh ? "fresh" : "stale", current.Hash, indexedHash,
                fresh ? $"CodeGraph is up to date for '{source}'." :
                    $"CodeGraph is stale for '{source}': C# source or build inputs changed since the last verified index. Reindex in the CodeGraph workbench.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(source, "unavailable", null, indexedHash,
                $"CodeGraph source could not be checked: {exception.Message}");
        }
    }

    private static Dictionary<string, object?>? ReadBaseline(SqliteConnection connection, string key)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT relative_source, source_hash, indexed_at FROM maf_source_fingerprints WHERE source_path = $path";
        command.Parameters.AddWithValue("$path", key);
        return ReadRows(command).FirstOrDefault();
    }

    private static string SourceKey(string source) => Path.GetFullPath(source).Replace('\\', '/');

    private static void InvalidateFingerprint(string database, string source)
    {
        using SqliteConnection connection = OpenWritable(database);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS maf_source_fingerprints (
                source_path TEXT PRIMARY KEY, relative_source TEXT NOT NULL,
                source_hash TEXT NOT NULL, indexed_at TEXT NOT NULL);
            DELETE FROM maf_source_fingerprints WHERE source_path = $path;
            """;
        command.Parameters.AddWithValue("$path", SourceKey(source));
        command.ExecuteNonQuery();
    }

    private static void SaveFingerprint(string database, string source, CodeGraphSourceFingerprint fingerprint)
    {
        using SqliteConnection connection = OpenWritable(database);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO maf_source_fingerprints (source_path, relative_source, source_hash, indexed_at)
            SELECT id, $relative, $hash, indexed_at FROM codebases WHERE id = $path;
            """;
        command.Parameters.AddWithValue("$path", SourceKey(source));
        command.Parameters.AddWithValue("$relative", fingerprint.SourceRelativePath);
        command.Parameters.AddWithValue("$hash", fingerprint.Hash);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The indexer did not record the requested codebase; its fingerprint was not saved.");
    }

    private static SqliteConnection OpenWritable(string database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
