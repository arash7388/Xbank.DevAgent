using Microsoft.Data.Sqlite;
using XBank.CodeGraph.Contracts;
using XBank.CodeGraph.Model;

namespace XBank.CodeGraph.Storage;

public sealed class GraphStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private SqliteTransaction? _tx;
    private SqliteCommand? _nodeCmd, _nodeEvidenceCmd, _edgeEvidenceCmd;
    private string _batchCodebaseId = "legacy";

    public GraphStore(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA temp_store=MEMORY;");
        Exec("PRAGMA cache_size=-64000;");
        CreateSchema();
        MigrateOwnershipSchema();
    }

    private void Exec(string sql)
    {
        using var c = _conn.CreateCommand();
        c.Transaction = _tx;
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private void CreateSchema()
    {
        Exec("""
        CREATE TABLE IF NOT EXISTS nodes (
            id          TEXT PRIMARY KEY,
            kind        TEXT NOT NULL,
            name        TEXT NOT NULL,
            full_name   TEXT NOT NULL,
            file        TEXT,
            line        INTEGER NOT NULL DEFAULT 0,
            signature   TEXT NOT NULL,
            body        TEXT,
            is_external INTEGER NOT NULL DEFAULT 0,
            degree      INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS edges (
            src      TEXT NOT NULL,
            dst      TEXT NOT NULL,
            kind     TEXT NOT NULL,
            src_file TEXT,
            count    INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY (src, dst, kind)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS ix_edges_src  ON edges(src);
        CREATE INDEX IF NOT EXISTS ix_edges_dst  ON edges(dst);
        CREATE INDEX IF NOT EXISTS ix_edges_file ON edges(src_file);
        CREATE INDEX IF NOT EXISTS ix_nodes_file ON nodes(file);
        CREATE INDEX IF NOT EXISTS ix_nodes_name ON nodes(name);

        CREATE TABLE IF NOT EXISTS files (
            codebase_id TEXT NOT NULL DEFAULT 'legacy',
            path TEXT NOT NULL,
            hash TEXT NOT NULL,
            PRIMARY KEY (codebase_id, path)
        );

        CREATE TABLE IF NOT EXISTS codebases (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            source_path TEXT NOT NULL,
            indexed_at TEXT NOT NULL
        );

        -- Evidence is the source of truth. The edges table is a derived, fast traversal index.
        CREATE TABLE IF NOT EXISTS node_evidence (
            node_id TEXT NOT NULL,
            codebase_id TEXT NOT NULL DEFAULT 'legacy',
            file    TEXT NOT NULL,
            line    INTEGER NOT NULL,
            PRIMARY KEY (node_id, codebase_id, file, line)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS edge_evidence (
            src   TEXT NOT NULL,
            dst   TEXT NOT NULL,
            kind  TEXT NOT NULL,
            codebase_id TEXT NOT NULL DEFAULT 'legacy',
            file  TEXT NOT NULL,
            count INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY (src, dst, kind, codebase_id, file)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS ix_edge_evidence_file ON edge_evidence(file);
        CREATE INDEX IF NOT EXISTS ix_node_evidence_file ON node_evidence(file);

        -- پیمایش دوطرفه: یال‌ها در هر دو جهت قابل دنبال کردن باشند
        CREATE VIEW IF NOT EXISTS edges_bi AS
            SELECT src AS a, dst AS b, kind, count, 'out' AS dir FROM edges
            UNION ALL
            SELECT dst AS a, src AS b, kind, count, 'in'  AS dir FROM edges;
        """);

        // جست‌وجوی زیررشته‌ای روی نام نمادها
        Exec("""
        CREATE VIRTUAL TABLE IF NOT EXISTS node_fts
        USING fts5(id UNINDEXED, name, full_name, tokenize='trigram');
        """);
    }

    // ---------- نوشتن دسته‌ای ----------

    public void BeginBatch(bool resetGraph = false, string codebaseId = "legacy")
    {
        _batchCodebaseId = codebaseId;
        _tx = _conn.BeginTransaction();

        if (resetGraph)
        {
            Exec("DELETE FROM edge_evidence;");
            Exec("DELETE FROM node_evidence;");
            Exec("DELETE FROM edges;");
            Exec("DELETE FROM nodes;");
            Exec("DELETE FROM node_fts;");
        }

        _nodeCmd = _conn.CreateCommand();
        _nodeCmd.Transaction = _tx;
        _nodeCmd.CommandText = """
            INSERT INTO nodes (id, kind, name, full_name, file, line, signature, body, is_external)
            VALUES ($id, $kind, $name, $full, $file, $line, $sig, $body, $ext)
            ON CONFLICT(id) DO UPDATE SET
                kind      = excluded.kind,
                name      = excluded.name,
                full_name = excluded.full_name,
                file      = COALESCE(excluded.file, nodes.file),
                line      = CASE WHEN excluded.file IS NULL THEN nodes.line ELSE excluded.line END,
                signature = excluded.signature,
                body      = COALESCE(excluded.body, nodes.body),
                is_external = excluded.is_external;
            """;
        foreach (var p in new[] { "$id", "$kind", "$name", "$full", "$file", "$line", "$sig", "$body", "$ext" })
            _nodeCmd.Parameters.Add(new SqliteParameter(p, SqliteType.Text));
        _nodeCmd.Prepare();

        _nodeEvidenceCmd = _conn.CreateCommand();
        _nodeEvidenceCmd.Transaction = _tx;
        _nodeEvidenceCmd.CommandText = """
            INSERT INTO node_evidence (node_id, codebase_id, file, line)
            VALUES ($id, $codebase, $file, $line)
            ON CONFLICT(node_id, codebase_id, file, line) DO NOTHING;
            """;
        foreach (var p in new[] { "$id", "$codebase", "$file", "$line" })
            _nodeEvidenceCmd.Parameters.Add(new SqliteParameter(p, SqliteType.Text));
        _nodeEvidenceCmd.Prepare();

        _edgeEvidenceCmd = _conn.CreateCommand();
        _edgeEvidenceCmd.Transaction = _tx;
        _edgeEvidenceCmd.CommandText = """
            INSERT INTO edge_evidence (src, dst, kind, codebase_id, file, count)
            VALUES ($src, $dst, $kind, $codebase, $file, 1)
            ON CONFLICT(src, dst, kind, codebase_id, file) DO UPDATE SET count = edge_evidence.count + 1;
            """;
        foreach (var p in new[] { "$src", "$dst", "$kind", "$codebase", "$file" })
            _edgeEvidenceCmd.Parameters.Add(new SqliteParameter(p, SqliteType.Text));
        _edgeEvidenceCmd.Prepare();
    }

    public void UpsertNode(NodeRow n, string? codebaseId = null)
    {
        var c = _nodeCmd!;
        c.Parameters["$id"].Value   = n.Id;
        c.Parameters["$kind"].Value = n.Kind;
        c.Parameters["$name"].Value = n.Name;
        c.Parameters["$full"].Value = n.FullName;
        c.Parameters["$file"].Value = (object?)n.File ?? DBNull.Value;
        c.Parameters["$line"].Value = n.Line;
        c.Parameters["$sig"].Value  = n.Signature;
        c.Parameters["$body"].Value = (object?)n.Body ?? DBNull.Value;
        c.Parameters["$ext"].Value  = n.IsExternal ? 1 : 0;
        c.ExecuteNonQuery();

        if (n.File is not null)
        {
            var evidence = _nodeEvidenceCmd!;
            evidence.Parameters["$id"].Value = n.Id;
            evidence.Parameters["$codebase"].Value = codebaseId ?? _batchCodebaseId;
            evidence.Parameters["$file"].Value = n.File;
            evidence.Parameters["$line"].Value = n.Line;
            evidence.ExecuteNonQuery();
        }
    }

    public void UpsertEdge(string src, string dst, string kind, string? srcFile, string? codebaseId = null)
    {
        if (src == dst) return;
        if (srcFile is null) return;
        var c = _edgeEvidenceCmd!;
        c.Parameters["$src"].Value  = src;
        c.Parameters["$dst"].Value  = dst;
        c.Parameters["$kind"].Value = kind;
        c.Parameters["$codebase"].Value = codebaseId ?? _batchCodebaseId;
        c.Parameters["$file"].Value = (object?)srcFile ?? DBNull.Value;
        c.ExecuteNonQuery();
    }

    public void CommitBatch()
    {
        _tx?.Commit();
        DisposeBatch();
    }

    private bool HasColumn(string table, string column)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = $"PRAGMA table_info({table})";
        using var r = c.ExecuteReader();
        while (r.Read()) if (r.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void MigrateOwnershipSchema()
    {
        if (!HasColumn("files", "codebase_id"))
            Exec("""
                ALTER TABLE files RENAME TO files_legacy;
                CREATE TABLE files (codebase_id TEXT NOT NULL, path TEXT NOT NULL, hash TEXT NOT NULL, PRIMARY KEY(codebase_id, path));
                INSERT INTO files SELECT 'legacy', path, hash FROM files_legacy;
                DROP TABLE files_legacy;
                """);
        if (!HasColumn("node_evidence", "codebase_id"))
            Exec("""
                DROP INDEX IF EXISTS ix_node_evidence_file;
                ALTER TABLE node_evidence RENAME TO node_evidence_legacy;
                CREATE TABLE node_evidence (node_id TEXT NOT NULL, codebase_id TEXT NOT NULL, file TEXT NOT NULL, line INTEGER NOT NULL, PRIMARY KEY(node_id, codebase_id, file, line)) WITHOUT ROWID;
                INSERT INTO node_evidence SELECT node_id, 'legacy', file, line FROM node_evidence_legacy;
                DROP TABLE node_evidence_legacy;
                CREATE INDEX ix_node_evidence_file ON node_evidence(file);
                """);
        if (!HasColumn("edge_evidence", "codebase_id"))
            Exec("""
                DROP INDEX IF EXISTS ix_edge_evidence_file;
                ALTER TABLE edge_evidence RENAME TO edge_evidence_legacy;
                CREATE TABLE edge_evidence (src TEXT NOT NULL, dst TEXT NOT NULL, kind TEXT NOT NULL, codebase_id TEXT NOT NULL, file TEXT NOT NULL, count INTEGER NOT NULL DEFAULT 1, PRIMARY KEY(src, dst, kind, codebase_id, file)) WITHOUT ROWID;
                INSERT INTO edge_evidence SELECT src, dst, kind, 'legacy', file, count FROM edge_evidence_legacy;
                DROP TABLE edge_evidence_legacy;
                CREATE INDEX ix_edge_evidence_file ON edge_evidence(file);
                """);
    }

    public void AbortBatch()
    {
        _tx?.Rollback();
        DisposeBatch();
    }

    private void DisposeBatch()
    {
        _nodeCmd?.Dispose(); _nodeEvidenceCmd?.Dispose(); _edgeEvidenceCmd?.Dispose(); _tx?.Dispose();
        _nodeCmd = null; _nodeEvidenceCmd = null; _edgeEvidenceCmd = null; _tx = null;
    }

    // ---------- به‌روزرسانی افزایشی ----------

    public void PurgeFiles(IEnumerable<string> files, string? codebaseId = null)
    {
        var ownsTransaction = _tx is null;
        using var tx = ownsTransaction ? _conn.BeginTransaction() : null;
        var activeTx = _tx ?? tx!;
        using var delEdges = _conn.CreateCommand();
        delEdges.Transaction = activeTx;
        delEdges.CommandText = "DELETE FROM edge_evidence WHERE file = $f AND codebase_id = $c";
        delEdges.Parameters.Add(new SqliteParameter("$f", SqliteType.Text));
        delEdges.Parameters.AddWithValue("$c", codebaseId ?? _batchCodebaseId);

        using var delNodes = _conn.CreateCommand();
        delNodes.Transaction = activeTx;
        delNodes.CommandText = "DELETE FROM node_evidence WHERE file = $f AND codebase_id = $c";
        delNodes.Parameters.Add(new SqliteParameter("$f", SqliteType.Text));
        delNodes.Parameters.AddWithValue("$c", codebaseId ?? _batchCodebaseId);

        foreach (var f in files)
        {
            delEdges.Parameters["$f"].Value = f;
            delEdges.ExecuteNonQuery();
            delNodes.Parameters["$f"].Value = f;
            delNodes.ExecuteNonQuery();
        }
        using var cleanNodes = _conn.CreateCommand();
        cleanNodes.Transaction = activeTx;
        cleanNodes.CommandText = "DELETE FROM nodes WHERE is_external = 0 AND NOT EXISTS (SELECT 1 FROM node_evidence ne WHERE ne.node_id = nodes.id);";
        cleanNodes.ExecuteNonQuery();
        if (ownsTransaction) tx!.Commit();
    }

    public Dictionary<string, string> GetFileHashes(string codebaseId = "legacy")
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT path, hash FROM files WHERE codebase_id = $c";
        c.Parameters.AddWithValue("$c", codebaseId);
        using var r = c.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    public void SetFileHashes(string codebaseId, IEnumerable<KeyValuePair<string, string>> hashes)
    {
        using var tx = _conn.BeginTransaction();
        using var c = _conn.CreateCommand();
        c.Transaction = tx;
        using var clear = _conn.CreateCommand();
        clear.Transaction = tx;
        clear.CommandText = "DELETE FROM files WHERE codebase_id = $c;";
        clear.Parameters.AddWithValue("$c", codebaseId);
        clear.ExecuteNonQuery();
        c.CommandText = """
            INSERT INTO files (codebase_id, path, hash) VALUES ($c, $p, $h)
            ON CONFLICT(codebase_id, path) DO UPDATE SET hash = excluded.hash;
            """;
        c.Parameters.Add(new SqliteParameter("$p", SqliteType.Text));
        c.Parameters.Add(new SqliteParameter("$h", SqliteType.Text));
        c.Parameters.AddWithValue("$c", codebaseId);
        foreach (var kv in hashes)
        {
            c.Parameters["$p"].Value = kv.Key;
            c.Parameters["$h"].Value = kv.Value;
            c.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void SetFileHashes(IEnumerable<KeyValuePair<string, string>> hashes) => SetFileHashes("legacy", hashes);

    public void UpsertCodebase(string id, string name, string sourcePath)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = """
            INSERT INTO codebases (id, name, source_path, indexed_at)
            VALUES ($id, $name, $path, $at)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name, source_path=excluded.source_path, indexed_at=excluded.indexed_at;
            """;
        c.Parameters.AddWithValue("$id", id);
        c.Parameters.AddWithValue("$name", name);
        c.Parameters.AddWithValue("$path", sourcePath);
        c.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        c.ExecuteNonQuery();
    }

    // ---------- پس‌پردازش ----------

    public void Finalize_()
    {
        // Re-materialize aggregate edges from their per-file evidence. This makes
        // a successful reindex idempotent and makes changed-file purges safe.
        Exec("DELETE FROM edge_evidence WHERE NOT EXISTS (SELECT 1 FROM nodes n WHERE n.id = edge_evidence.src) OR NOT EXISTS (SELECT 1 FROM nodes n WHERE n.id = edge_evidence.dst);");
        Exec("DELETE FROM edges;");
        Exec("INSERT INTO edges (src, dst, kind, count) SELECT src, dst, kind, SUM(count) FROM edge_evidence GROUP BY src, dst, kind;");
        Exec("DELETE FROM nodes WHERE is_external = 1 AND NOT EXISTS (SELECT 1 FROM edges e WHERE e.src = nodes.id OR e.dst = nodes.id);");
        Exec("""
        UPDATE nodes SET
            file = (SELECT ne.file FROM node_evidence ne WHERE ne.node_id = nodes.id ORDER BY ne.codebase_id, ne.file, ne.line LIMIT 1),
            line = (SELECT ne.line FROM node_evidence ne WHERE ne.node_id = nodes.id ORDER BY ne.codebase_id, ne.file, ne.line LIMIT 1)
        WHERE is_external = 0
          AND file IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM node_evidence ne WHERE ne.node_id = nodes.id AND ne.file = nodes.file);
        """);
        // درجه‌ی هر گره: برای شناسایی گره‌های پرتکرار (hub) مثل DbContext
        Exec("""
        -- Query each indexed endpoint directly. Older SQLite versions scan the
        -- entire UNION ALL view for every node in a correlated edges_bi query.
        -- A self-loop contributes once in each direction, as in edges_bi.
        UPDATE nodes SET degree =
            (SELECT COUNT(*) FROM edges WHERE src = nodes.id) +
            (SELECT COUNT(*) FROM edges WHERE dst = nodes.id);
        """);

        Exec("DELETE FROM node_fts;");
        Exec("INSERT INTO node_fts (id, name, full_name) SELECT id, name, full_name FROM nodes;");
        Exec("ANALYZE;");
    }

    // ---------- خواندن ----------

    private static NodeRow Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5),
        r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
        r.GetInt32(8) != 0, r.GetInt32(9));

    private const string Cols =
        "id, kind, name, full_name, file, line, signature, body, is_external, degree";

    public NodeRow? GetNode(string id)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = $"SELECT {Cols} FROM nodes WHERE id = $id";
        c.Parameters.AddWithValue("$id", id);
        using var r = c.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public List<NodeRow> GetNodes(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        if (list.Count == 0) return [];
        var ps = string.Join(",", list.Select((_, i) => $"$p{i}"));
        using var c = _conn.CreateCommand();
        c.CommandText = $"SELECT {Cols} FROM nodes WHERE id IN ({ps})";
        for (int i = 0; i < list.Count; i++) c.Parameters.AddWithValue($"$p{i}", list[i]);
        var res = new List<NodeRow>();
        using var r = c.ExecuteReader();
        while (r.Read()) res.Add(Read(r));
        return res;
    }

    public List<NodeRow> Search(string term, int limit = 25)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = $"""
            SELECT {string.Join(",", Cols.Split(',').Select(x => "n." + x.Trim()))}
            FROM node_fts f
            JOIN nodes n ON n.id = f.id
            WHERE node_fts MATCH $q AND n.is_external = 0
            ORDER BY bm25(node_fts), n.degree DESC
            LIMIT $lim;
            """;
        c.Parameters.AddWithValue("$q", Fts(term));
        c.Parameters.AddWithValue("$lim", limit);
        var res = new List<NodeRow>();
        using var r = c.ExecuteReader();
        while (r.Read()) res.Add(Read(r));
        return res;
    }

    private static string Fts(string term)
    {
        var clean = new string(term.Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.' or ' ').ToArray());
        var parts = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                         .Where(p => p.Length >= 3)
                         .Select(p => $"\"{p}\"");
        var q = string.Join(" ", parts);
        return string.IsNullOrWhiteSpace(q) ? "\"zzzznomatch\"" : q;
    }

    public List<Neighbor> Neighbors(string id)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT b, kind, dir, count FROM edges_bi WHERE a = $id";
        c.Parameters.AddWithValue("$id", id);
        var res = new List<Neighbor>();
        using var r = c.ExecuteReader();
        while (r.Read()) res.Add(new Neighbor(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));
        return res;
    }

    public (int nodes, int edges) Stats()
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "SELECT (SELECT COUNT(*) FROM nodes), (SELECT COUNT(*) FROM edges)";
        using var r = c.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), r.GetInt32(1));
    }

    public void Dispose()
    {
        AbortBatch();
        _conn.Dispose();
    }
}
