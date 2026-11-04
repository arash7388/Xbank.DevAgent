using Xbank.DevAgent.Api;

namespace Xbank.DevAgent.Tests;

public sealed class CodingRuleTests
{
    [Fact]
    public void RulesPersistAndSnapshotsRemainStableAfterUpdatesAndDeletes()
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-coding-rules-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "rules.db");
            var store = new CodingRuleStore(path);
            Assert.Equal("", store.Snapshot(false));
            Assert.Equal("", store.Snapshot(true));
            CodingRule rule = store.Save(null, new(" Naming ", " Use PascalCase. "));
            string snapshot = store.Snapshot(true);
            var reopened = new CodingRuleStore(path);
            Assert.Equal(rule, Assert.Single(reopened.List()));
            reopened.Save(rule.Id, new("Naming", "Use camelCase."));
            Assert.Contains("Use PascalCase.", snapshot);
            Assert.DoesNotContain("Use PascalCase.", reopened.Snapshot(true));
            reopened.Delete(rule.Id);
            Assert.Empty(store.List());
            Assert.Contains("Use PascalCase.", snapshot);
            Assert.Throws<KeyNotFoundException>(() => store.Delete(rule.Id));
            Assert.Throws<ArgumentException>(() => store.Save(null, new("", "rule")));
            Assert.Throws<ArgumentException>(() => store.Save(null, new("title", new string('x', 8001))));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LegacyRulesMigrateAndDisabledRulesStayOutOfRunSnapshots()
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-coding-enabled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "rules.db");
            Guid id = Guid.NewGuid();
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE coding_rules (id TEXT PRIMARY KEY, title TEXT NOT NULL, content TEXT NOT NULL); INSERT INTO coding_rules VALUES ($id, 'Legacy', 'Original convention')";
                command.Parameters.AddWithValue("$id", id.ToString());
                command.ExecuteNonQuery();
            }
            var store = new CodingRuleStore(path);
            Assert.True(Assert.Single(store.List()).Enabled);
            string snapshot = store.Snapshot(true);
            store.SetEnabled(id, false);
            Assert.False(Assert.Single(new CodingRuleStore(path).List()).Enabled);
            Assert.Equal("", store.Snapshot(true));
            Assert.Contains("Original convention", snapshot);
            Assert.False(store.Save(id, new("Legacy", "Edited disabled convention")).Enabled);
            CodingRule active = store.Save(null, new("Active", "Included convention"));
            Assert.True(active.Enabled);
            Assert.Contains("Included convention", store.Snapshot(true));
            Assert.DoesNotContain("Edited disabled convention", store.Snapshot(true));
            store.SetEnabled(id, true);
            Assert.Contains("Edited disabled convention", store.Snapshot(true));
            Assert.Throws<KeyNotFoundException>(() => store.SetEnabled(Guid.NewGuid(), true));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

}
