using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Maf.Handoff.Core;
using Microsoft.Data.Sqlite;

namespace Maf.Handoff.Api;

public sealed record CodeGraphIndexRequest(
    string SourcePath,
    string DatabasePath,
    bool Incremental = true,
    bool IncludeExternal = false,
    bool RestoreDependencies = true);
public sealed record CodeGraphFindSymbolRequest(string DatabasePath, string Name, int Limit = 15);
public sealed record CodeGraphNeighborsRequest(string DatabasePath, string Id, int Limit = 100);
public sealed record CodeGraphContextRequest(string DatabasePath, string Query, int MaxNodes = 25, int TokenBudget = 5000);
public sealed record CodeGraphContextByIdRequest(string DatabasePath, string Id, int MaxNodes = 25, int TokenBudget = 5000);

public sealed class CodeGraphService(IConfiguration configuration) : ICodeGraphTools
{
    private static readonly HashSet<string> AllowedTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "codebases", "nodes", "edges", "files", "node_evidence", "edge_evidence"
    };

    private readonly object _statusLock = new();
    private readonly string _rootPath = Path.GetFullPath(configuration["CodeGraph:RootPath"] ?? @"E:\CodeGraph");
    private readonly string _agentDatabasePath = Path.GetFullPath(
        configuration["CodeGraph:DatabasePath"] ??
        Path.Combine(configuration["CodeGraph:RootPath"] ?? @"E:\CodeGraph", "codegraph.db"));
    private CodeGraphIndexStatus _status = new("idle", null, null, null, null, null, []);
    private bool _sawWorkspaceFailure;

    public object GetConfiguration() => new
    {
        rootPath = _rootPath,
        indexerProject = Path.Combine(_rootPath, "CodeGraph", "CodeGraph.csproj"),
        defaultDatabasePath = _agentDatabasePath,
        tools = new[] { "find_symbol", "get_neighbors", "get_context", "get_context_by_id" },
        tables = AllowedTables.OrderBy(x => x)
    };

    public void EnsureAvailable(string repositoryUrl)
    {
        using SqliteConnection connection = Open(_agentDatabasePath);
        HashSet<string> available = GetAvailableTables(connection);
        if (!available.Contains("nodes") || !available.Contains("edges") || !available.Contains("codebases"))
        {
            throw new ConfigurationException(
                $"Protected CodeGraph database '{_agentDatabasePath}' is missing required graph tables.");
        }

        string repositoryName = GetRepositoryName(repositoryUrl);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name, source_path FROM codebases";
        IReadOnlyList<Dictionary<string, object?>> codebases = ReadRows(command);
        bool matchesRepository = codebases.Any(row =>
            string.Equals(row["name"]?.ToString(), repositoryName, StringComparison.OrdinalIgnoreCase) ||
            PathContainsRepositoryName(row["source_path"]?.ToString(), repositoryName));
        if (!matchesRepository)
        {
            string indexed = string.Join(", ", codebases
                .Select(row => row["name"]?.ToString())
                .Where(name => !string.IsNullOrWhiteSpace(name)));
            throw new ConfigurationException(
                $"Protected CodeGraph database '{_agentDatabasePath}' does not contain an index for " +
                $"repository '{repositoryName}'. Indexed codebases: {(indexed.Length == 0 ? "none" : indexed)}.");
        }
    }

    public string FindSymbol(string name, int limit = 15) => JsonSerializer.Serialize(
        FindSymbol(new CodeGraphFindSymbolRequest(_agentDatabasePath, name, limit)));

    public string GetNeighbors(string id, int limit = 100) => JsonSerializer.Serialize(
        GetNeighbors(new CodeGraphNeighborsRequest(_agentDatabasePath, id, limit)));

    public string GetContext(string query, int maxNodes = 15, int tokenBudget = 2_500) =>
        GetContext(new CodeGraphContextRequest(
            _agentDatabasePath,
            query,
            maxNodes,
            tokenBudget));

    public string GetContextById(string id, int maxNodes = 15, int tokenBudget = 2_500) =>
        GetContextById(new CodeGraphContextByIdRequest(
            _agentDatabasePath,
            id,
            maxNodes,
            tokenBudget));

    public CodeGraphIndexStatus StartIndex(CodeGraphIndexRequest request)
    {
        var source = RequireExistingSource(request.SourcePath);
        var database = RequireDatabasePath(request.DatabasePath, mustExist: false);
        var project = Path.Combine(_rootPath, "CodeGraph", "CodeGraph.csproj");
        if (!File.Exists(project)) throw new ConfigurationException($"CodeGraph indexer was not found at '{project}'. Configure CodeGraph:RootPath.");

        lock (_statusLock)
        {
            if (_status.State == "running") throw new InvalidOperationException("A CodeGraph indexing job is already running.");
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            _sawWorkspaceFailure = false;
            _status = new("running", source, database, DateTimeOffset.UtcNow, null, null, []);
            _ = Task.Run(() => RunIndexerAsync(project, source, database, request));
            return _status;
        }
    }

    public CodeGraphIndexStatus GetIndexStatus()
    {
        lock (_statusLock) return _status with { Log = [.. _status.Log] };
    }

    private async Task RunIndexerAsync(string project, string source, string database, CodeGraphIndexRequest request)
    {
        try
        {
            var repairIncompleteIndex = request.RestoreDependencies && HasMissingFallbackFolderReferences(source);
            var restoreFailed = false;
            if (request.RestoreDependencies)
            {
                AppendLog("Preparing solution dependencies (dotnet restore --force)...");
                var restoreExitCode = await RunProcessAsync(
                    Path.GetDirectoryName(source)!,
                    ["restore", source, "--force", "--no-cache"]);
                if (restoreExitCode != 0)
                {
                    restoreFailed = true;
                    AppendLog($"Dependency restore exited with code {restoreExitCode}. Continuing with the assets that are available; the result may be incomplete.");
                }
            }

            if (repairIncompleteIndex)
                AppendLog("Obsolete NuGet fallback-folder references were repaired; running a full reindex so previously skipped projects are rebuilt.");

            AppendLog("Starting CodeGraph indexer...");
            var indexArguments = new List<string>
            {
                "run", "--project", project, "-c", "Release", "--no-launch-profile", "--",
                "index", "--solution", source, "--db", database
            };
            if (request.Incremental && !repairIncompleteIndex) indexArguments.Add("--incremental");
            if (request.IncludeExternal) indexArguments.Add("--include-external");
            var exitCode = await RunProcessAsync(_rootPath, indexArguments);
            bool hasWorkspaceFailures;
            lock (_statusLock) hasWorkspaceFailures = _sawWorkspaceFailure;
            var hasWarnings = restoreFailed || hasWorkspaceFailures;
            if (exitCode == 0 && hasWarnings)
                AppendLog("Index completed with warnings. Some projects or package references could not be loaded; review the messages above.");

            lock (_statusLock)
                _status = _status with
                {
                    State = exitCode == 0
                        ? hasWarnings ? "completed_with_warnings" : "completed"
                        : "failed",
                    CompletedAt = DateTimeOffset.UtcNow,
                    Error = exitCode == 0 ? null : $"Indexer exited with code {exitCode}."
                };
        }
        catch (Exception exception)
        {
            lock (_statusLock) _status = _status with { State = "failed", CompletedAt = DateTimeOffset.UtcNow, Error = exception.Message };
        }
    }

    private async Task<int> RunProcessAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => AppendLog(e.Data);
        process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        if (!process.Start()) throw new InvalidOperationException($"The dotnet process could not be started in '{workingDirectory}'.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static bool HasMissingFallbackFolderReferences(string source)
    {
        const string obsoleteFolder = @"C:\Program Files\dotnet\sdk\NuGetFallbackFolder";
        if (Directory.Exists(obsoleteFolder)) return false;

        try
        {
            var root = Path.GetDirectoryName(source)!;
            return Directory.EnumerateFiles(root, "project.assets.json", SearchOption.AllDirectories)
                .Any(path => File.ReadLines(path).Any(line => line.Contains(obsoleteFolder, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var isPackageApproximation = IsPackageApproximationWarning(line);
        if (isPackageApproximation)
            line = line.Replace("[msbuild] Msbuild failed when processing", "[msbuild warning] Package version substituted while processing", StringComparison.OrdinalIgnoreCase);
        lock (_statusLock)
        {
            if (!isPackageApproximation && line.StartsWith("[msbuild]", StringComparison.OrdinalIgnoreCase)) _sawWorkspaceFailure = true;
            var log = _status.Log.ToList();
            log.Add(line);
            if (log.Count > 200) log.RemoveRange(0, log.Count - 200);
            _status = _status with { Log = log };
        }
    }

    private static bool IsPackageApproximationWarning(string line) =>
        line.StartsWith("[msbuild]", StringComparison.OrdinalIgnoreCase)
        && line.Contains(" depends on ", StringComparison.OrdinalIgnoreCase)
        && line.Contains(" was not found. ", StringComparison.OrdinalIgnoreCase)
        && line.Contains(" was resolved instead.", StringComparison.OrdinalIgnoreCase);

    public object GetDatabaseOverview(string dbPath)
    {
        using var connection = Open(dbPath);
        var available = GetAvailableTables(connection);
        if (!available.Contains("nodes") || !available.Contains("edges"))
            throw new ArgumentException("The selected SQLite file is not a CodeGraph database (nodes/edges tables are missing).");
        var counts = AllowedTables.Where(available.Contains).ToDictionary(table => table, table => ScalarLong(connection, $"SELECT COUNT(*) FROM {table}"));
        return new
        {
            databasePath = Path.GetFullPath(dbPath),
            sizeBytes = new FileInfo(Path.GetFullPath(dbPath)).Length,
            counts,
            codebases = available.Contains("codebases")
                ? QueryRows(connection, "SELECT id, name, source_path, indexed_at FROM codebases ORDER BY indexed_at DESC LIMIT 20")
                : [],
            nodeKinds = QueryRows(connection, "SELECT kind, COUNT(*) AS count FROM nodes GROUP BY kind ORDER BY count DESC"),
            edgeKinds = QueryRows(connection, "SELECT kind, COUNT(*) AS count, SUM(count) AS evidence_count FROM edges GROUP BY kind ORDER BY count DESC"),
            topSymbols = QueryRows(connection, "SELECT id, kind, name, full_name, file, line, signature, degree FROM nodes WHERE is_external = 0 ORDER BY degree DESC LIMIT 20")
        };
    }

    public object GetTable(string dbPath, string table, int limit, int offset, string? search)
    {
        if (!AllowedTables.Contains(table)) throw new ArgumentException($"Table '{table}' is not available.");
        limit = Math.Clamp(limit, 1, 250);
        offset = Math.Max(0, offset);
        using var connection = Open(dbPath);
        if (!GetAvailableTables(connection).Contains(table))
            throw new ArgumentException($"Table '{table}' does not exist in this CodeGraph database. Reindex it to upgrade the schema.");
        var columns = GetColumns(connection, table);
        var searchable = columns.Where(c => c.Type.Contains("TEXT", StringComparison.OrdinalIgnoreCase)).Select(c => c.Name).ToList();
        using var command = connection.CreateCommand();
        var where = string.IsNullOrWhiteSpace(search) || searchable.Count == 0
            ? ""
            : " WHERE " + string.Join(" OR ", searchable.Select(c => $"CAST(\"{c}\" AS TEXT) LIKE $search ESCAPE '!'"));
        command.CommandText = $"SELECT * FROM \"{table}\"{where} LIMIT $limit OFFSET $offset";
        if (where.Length > 0) command.Parameters.AddWithValue("$search", $"%{EscapeLike(search!.Trim())}%");
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        return new { table, columns = columns.Select(c => c.Name), rows = ReadRows(command), limit, offset };
    }

    public IReadOnlyList<Dictionary<string, object?>> FindSymbol(CodeGraphFindSymbolRequest request)
    {
        var query = RequireText(request.Name, "symbol name", 256);
        using var connection = Open(request.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, kind, full_name, file, line, signature, degree
            FROM nodes WHERE is_external = 0 AND (name LIKE $contains ESCAPE '!' OR full_name LIKE $contains ESCAPE '!')
            ORDER BY CASE WHEN name = $exact THEN 0 WHEN name LIKE $prefix ESCAPE '!' THEN 1 ELSE 2 END, degree DESC, LENGTH(name)
            LIMIT $limit
            """;
        var escaped = EscapeLike(query);
        command.Parameters.AddWithValue("$contains", $"%{escaped}%");
        command.Parameters.AddWithValue("$exact", query);
        command.Parameters.AddWithValue("$prefix", $"{escaped}%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(request.Limit, 1, 100));
        return ReadRows(command);
    }

    public IReadOnlyList<Dictionary<string, object?>> GetNeighbors(CodeGraphNeighborsRequest request)
    {
        var id = RequireText(request.Id, "symbol ID", 2048);
        using var connection = Open(request.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'outgoing' AS direction, e.kind, e.count AS evidence_count,
                   n.id, n.name, n.kind AS node_kind, n.full_name, n.file, n.line, n.signature
            FROM edges e JOIN nodes n ON n.id = e.dst WHERE e.src = $id
            UNION ALL
            SELECT 'incoming', e.kind, e.count, n.id, n.name, n.kind, n.full_name, n.file, n.line, n.signature
            FROM edges e JOIN nodes n ON n.id = e.src WHERE e.dst = $id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$limit", Math.Clamp(request.Limit, 1, 250));
        return ReadRows(command);
    }

    public string GetContext(CodeGraphContextRequest request)
    {
        var matches = FindSymbol(new(request.DatabasePath, request.Query, 3));
        return matches.Count == 0
            ? $"No symbol matching '{request.Query}' exists in the graph."
            : BuildContext(request.DatabasePath, (string)matches[0]["id"]!, request.MaxNodes, request.TokenBudget,
                matches.Skip(1).Select(x => $"{x["name"]} ({x["kind"]}) - {x["id"]}").ToList());
    }

    public string GetContextById(CodeGraphContextByIdRequest request) =>
        BuildContext(request.DatabasePath, RequireText(request.Id, "symbol ID", 2048), request.MaxNodes, request.TokenBudget, []);

    private string BuildContext(string dbPath, string rootId, int maxNodes, int tokenBudget, IReadOnlyList<string> alternatives)
    {
        maxNodes = Math.Clamp(maxNodes, 1, 100);
        var charBudget = Math.Clamp(tokenBudget, 250, 20_000) * 4;
        using var connection = Open(dbPath);
        var root = GetNode(connection, rootId, includeBody: true);
        if (root is null) return $"No symbol with ID '{rootId}' exists in the graph.";

        var edgeCosts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["RESOLVES_TO"] = .4, ["IMPLEMENTS"] = .6, ["OVERRIDES"] = .6,
            ["CALLS"] = 1, ["INHERITS"] = 1, ["CONTAINS"] = 1.2,
            ["HANDLES"] = .35, ["ENDPOINT_OF"] = .35, ["INSTANTIATES"] = 1,
            ["READS"] = 1.4, ["WRITES"] = 1.4, ["USES_TYPE"] = 2.5
        };
        var queue = new PriorityQueue<(string Id, string Relation, int Depth), double>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var best = new Dictionary<string, double>(StringComparer.Ordinal) { [rootId] = 0 };
        var related = new List<(Dictionary<string, object?> Node, string Relation, int Depth)>();
        queue.Enqueue((rootId, "ROOT", 0), 0);
        while (queue.TryDequeue(out var current, out var distance) && visited.Count < maxNodes)
        {
            if (!visited.Add(current.Id) || distance > best[current.Id]) continue;
            if (current.Id != rootId)
                related.Add((GetNode(connection, current.Id, includeBody: false)!, current.Relation, current.Depth));

            foreach (var neighbor in GetNeighborRows(connection, current.Id, 250))
            {
                var id = (string)neighbor["id"]!;
                if (visited.Contains(id)) continue;
                var relation = neighbor["direction"]?.ToString() == "incoming" ? $"{neighbor["kind"]}_BY" : neighbor["kind"]!.ToString()!;
                var cost = edgeCosts.GetValueOrDefault(neighbor["kind"]!.ToString()!, 2);
                if (neighbor["direction"]?.ToString() == "incoming") cost *= 1.4;
                var nextDistance = distance + cost;
                if (nextDistance > 4 || best.TryGetValue(id, out var oldDistance) && nextDistance >= oldDistance) continue;
                best[id] = nextDistance;
                queue.Enqueue((id, relation, current.Depth + 1), nextDistance);
            }
        }

        var output = new StringBuilder();
        output.AppendLine($"# Context: {root["name"]}").AppendLine($"`{root["id"]}`")
            .AppendLine($"- kind: {root["kind"]}").AppendLine($"- file: {root["file"]}:{root["line"]}")
            .AppendLine($"- signature: `{root["signature"]}`").AppendLine($"- degree: {root["degree"]}").AppendLine();
        if (root["body"] is string body && body.Length > 0)
            output.AppendLine("## Source").AppendLine("```csharp").AppendLine(Truncate(body, 6000)).AppendLine("```").AppendLine();
        foreach (var group in related.GroupBy(x => x.Relation))
        {
            output.AppendLine($"## {group.Key}");
            foreach (var item in group)
            {
                var line = $"- **{item.Node["name"]}** `{item.Node["kind"]}` — `{item.Node["signature"]}`\n  <{item.Node["file"]}:{item.Node["line"]}> · depth={item.Depth}, degree={item.Node["degree"]}\n";
                if (output.Length + line.Length > charBudget) return output.AppendLine("- … (truncated to token budget)").ToString();
                output.Append(line);
            }
            output.AppendLine();
        }
        if (alternatives.Count > 0) output.AppendLine("## Other matches").AppendJoin('\n', alternatives.Select(x => $"- {x}"));
        return output.Length <= charBudget ? output.ToString() : output.ToString(0, charBudget);
    }

    private static IReadOnlyList<Dictionary<string, object?>> GetNeighborRows(SqliteConnection connection, string id, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'outgoing' direction, e.kind, n.id FROM edges e JOIN nodes n ON n.id=e.dst WHERE e.src=$id
            UNION ALL SELECT 'incoming', e.kind, n.id FROM edges e JOIN nodes n ON n.id=e.src WHERE e.dst=$id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$limit", limit);
        return ReadRows(command);
    }

    private static Dictionary<string, object?>? GetNode(SqliteConnection connection, string id, bool includeBody)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, kind, name, full_name, file, line, signature, {(includeBody ? "body" : "NULL AS body")}, is_external, degree FROM nodes WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return ReadRows(command).FirstOrDefault();
    }

    private static SqliteConnection Open(string dbPath)
    {
        var path = RequireDatabasePath(dbPath, mustExist: true);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared }.ToString());
        connection.Open();
        return connection;
    }

    private static string RequireExistingSource(string value)
    {
        var path = Path.GetFullPath(RequireText(value, "solution or project path", 4096));
        if (!File.Exists(path)) throw new ArgumentException($"Solution or project '{path}' does not exist.");
        if (!new[] { ".sln", ".csproj" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("CodeGraph accepts a .sln or .csproj file.");
        return path;
    }

    private static string RequireDatabasePath(string value, bool mustExist)
    {
        var path = Path.GetFullPath(RequireText(value, "database path", 4096));
        if (!path.EndsWith(".db", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The database path must end in .db or .sqlite.");
        if (mustExist && !File.Exists(path)) throw new ArgumentException($"CodeGraph database '{path}' does not exist.");
        return path;
    }

    private static string RequireText(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"A {name} is required.");
        value = value.Trim();
        if (value.Length > maxLength) throw new ArgumentException($"The {name} is limited to {maxLength} characters.");
        return value;
    }

    private static string GetRepositoryName(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out Uri? uri))
        {
            throw new ConfigurationException("A valid repository URL is required for protected CodeGraph validation.");
        }

        string name = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault() ?? "";
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static bool PathContainsRepositoryName(string? sourcePath, string repositoryName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        string normalized = sourcePath.Replace('\\', '/');
        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, repositoryName, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(
                Path.GetFileNameWithoutExtension(normalized),
                repositoryName,
                StringComparison.OrdinalIgnoreCase);
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static IReadOnlyList<Dictionary<string, object?>> QueryRows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return ReadRows(command);
    }

    private static IReadOnlyList<Dictionary<string, object?>> ReadRows(SqliteCommand command)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static IReadOnlyList<(string Name, string Type)> GetColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand(); command.CommandText = $"PRAGMA table_info(\"{table}\")";
        var result = new List<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add((reader.GetString(1), reader.GetString(2)));
        return result;
    }

    private static HashSet<string> GetAvailableTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private static string EscapeLike(string value) => value.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + " …";
}

public sealed record CodeGraphIndexStatus(
    string State,
    string? SourcePath,
    string? DatabasePath,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    IReadOnlyList<string> Log);
