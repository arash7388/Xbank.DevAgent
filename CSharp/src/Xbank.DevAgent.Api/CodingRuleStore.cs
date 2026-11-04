using Microsoft.Data.Sqlite;

namespace Xbank.DevAgent.Api;

public sealed record CodingRule(Guid Id, string Title, string Content, bool Enabled = true);
public sealed record SaveCodingRuleRequest(string Title, string Content, bool? Enabled = null);
public sealed record SetCodingRuleEnabledRequest(bool Enabled);

public sealed class CodingRuleStore
{
    private readonly string _connectionString;

    public CodingRuleStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS coding_rules (id TEXT PRIMARY KEY, title TEXT NOT NULL, content TEXT NOT NULL)";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(coding_rules)";
        bool hasEnabled = false;
        using (var reader = command.ExecuteReader())
            while (reader.Read()) hasEnabled |= reader.GetString(1) == "enabled";
        if (!hasEnabled)
        {
            command.CommandText = "ALTER TABLE coding_rules ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public IReadOnlyList<CodingRule> List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, content, enabled FROM coding_rules ORDER BY title COLLATE NOCASE, id";
        using var reader = command.ExecuteReader();
        List<CodingRule> rules = [];
        while (reader.Read()) rules.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        return rules;
    }

    public CodingRule Save(Guid? id, SaveCodingRuleRequest request)
    {
        string title = request.Title?.Trim() ?? "";
        string content = request.Content?.Trim() ?? "";
        if (title.Length is < 1 or > 160 || content.Length is < 1 or > 8000)
            throw new ArgumentException("Provide a title (1–160 characters) and rule text (1–8,000 characters).");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(length(title) + length(content)), 0) FROM coding_rules WHERE id <> $id";
        command.Parameters.AddWithValue("$id", id?.ToString() ?? "");
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            if (reader.GetInt64(0) >= 100 || reader.GetInt64(1) + title.Length + content.Length > 50000)
                throw new ArgumentException("Coding Rules supports up to 100 rules and 50,000 characters in total.");
        }
        Guid ruleId = id ?? Guid.NewGuid();
        command.CommandText = id is null
            ? "INSERT INTO coding_rules (id, title, content, enabled) VALUES ($id, $title, $content, COALESCE($enabled, 1)) RETURNING enabled"
            : "UPDATE coding_rules SET title = $title, content = $content, enabled = COALESCE($enabled, enabled) WHERE id = $id RETURNING enabled";
        command.Parameters["$id"].Value = ruleId.ToString();
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$enabled", request.Enabled is bool enabled ? (object)(enabled ? 1 : 0) : DBNull.Value);
        object? savedEnabled = command.ExecuteScalar();
        if (savedEnabled is null) throw new KeyNotFoundException("Coding rule not found.");
        transaction.Commit();
        return new(ruleId, title, content, Convert.ToInt64(savedEnabled) != 0);
    }

    public void SetEnabled(Guid id, bool enabled)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE coding_rules SET enabled = $enabled WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException("Coding rule not found.");
    }

    public void Delete(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM coding_rules WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException("Coding rule not found.");
    }

    public string Snapshot(bool include)
    {
        if (!include) return "";
        var rules = List().Where(rule => rule.Enabled);
        return string.Join("\n\n", rules.Select(rule => $"{rule.Title}\n{rule.Content}"));
    }
}
