using XBank.CodeGraph.Contracts;
using Microsoft.Data.Sqlite;

namespace XBank.CodeGraph.McpServer.Retrieval;

/// <summary>Read-only access to the database produced by CodeGraph.</summary>
public sealed class GraphStore(string dbPath)
{
    private const string NodeColumns =
        "id, kind, name, full_name, file, line, signature, body, is_external, degree";

    private readonly string _cs = new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_cs);
        connection.Open();
        return connection;
    }

    public List<GraphNode> FindNodes(string query, int limit = 15)
    {
        query = ValidateQuery(query);
        limit = Math.Clamp(limit, 1, 100);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {NodeColumns}
            FROM nodes
            WHERE is_external = 0
              AND (name LIKE $contains ESCAPE '!' OR full_name LIKE $contains ESCAPE '!')
            ORDER BY CASE WHEN name = $exact THEN 0 WHEN name LIKE $prefix ESCAPE '!' THEN 1 ELSE 2 END,
                     degree DESC, LENGTH(name)
            LIMIT $limit;
            """;
        var escaped = EscapeLike(query);
        cmd.Parameters.AddWithValue("$contains", $"%{escaped}%");
        cmd.Parameters.AddWithValue("$exact", query);
        cmd.Parameters.AddWithValue("$prefix", $"{escaped}%");
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadNodes(cmd);
    }

    public GraphNode? GetNode(string id, bool includeBody = false)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 2048) return null;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {NodeColumns} FROM nodes WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadNode(reader, includeBody) : null;
    }

    public List<(GraphEdge Edge, GraphNode Node, bool Outgoing)> GetNeighbors(string id, int limit = 250)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 2048) return [];
        limit = Math.Clamp(limit, 1, 1000);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT e.src, e.dst, e.kind, e.count, {Prefix("n")}, 1 AS outgoing
            FROM edges e JOIN nodes n ON n.id = e.dst
            WHERE e.src = $id
            UNION ALL
            SELECT e.src, e.dst, e.kind, e.count, {Prefix("n")}, 0 AS outgoing
            FROM edges e JOIN nodes n ON n.id = e.src
            WHERE e.dst = $id
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<(GraphEdge, GraphNode, bool)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var edge = new GraphEdge(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
            list.Add((edge, ReadNode(reader, includeBody: false, offset: 4), reader.GetInt32(14) == 1));
        }
        return list;
    }

    public IReadOnlyList<GraphEdge> GetEdges(string id, int limit = 250) =>
        GetNeighbors(id, limit).Select(item => item.Edge).ToList();

    private static List<GraphNode> ReadNodes(SqliteCommand command)
    {
        var list = new List<GraphNode>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) list.Add(ReadNode(reader, includeBody: false));
        return list;
    }

    private static GraphNode ReadNode(SqliteDataReader reader, bool includeBody, int offset = 0) => new(
        reader.GetString(offset),
        reader.GetString(offset + 1),
        reader.GetString(offset + 2),
        reader.GetString(offset + 3),
        reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
        reader.GetInt32(offset + 5),
        reader.GetString(offset + 6),
        includeBody && !reader.IsDBNull(offset + 7) ? reader.GetString(offset + 7) : null,
        reader.GetInt32(offset + 8) != 0,
        reader.GetInt32(offset + 9));

    private static string Prefix(string alias) => string.Join(", ", NodeColumns.Split(", ").Select(column => $"{alias}.{column}"));

    private static string ValidateQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("A non-empty symbol query is required.", nameof(query));
        if (query.Length > 256) throw new ArgumentOutOfRangeException(nameof(query), "The symbol query is limited to 256 characters.");
        return query.Trim();
    }

    private static string EscapeLike(string value) => value.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
}
