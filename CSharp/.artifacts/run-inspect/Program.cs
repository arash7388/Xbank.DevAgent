using System;
using Microsoft.Data.Sqlite;
using var db = new SqliteConnection("Data Source=C:/Users/a.masir/source/repos/MAF/MAF/CSharp/src/Maf.Handoff.Api/data/llm-checkpoints.db;Mode=ReadOnly");
db.Open();
using var cmd = db.CreateCommand();
cmd.CommandText = args.Length > 0 ? args[0] : "SELECT name, sql FROM sqlite_master WHERE type='table' AND name LIKE '%run%'";
using var reader = cmd.ExecuteReader();
while (reader.Read()) { for (int i=0;i<reader.FieldCount;i++) Console.WriteLine(reader.GetName(i)+": "+reader.GetValue(i)); }
