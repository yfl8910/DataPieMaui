using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DBUtil;

namespace DataPieCore;

internal static partial class SQLiteProcedureScripts
{
    private static readonly object EditLock = new();
    private static string ScriptPath(string connectionString, string name)
        => Path.Combine(Folder(new SQLiteConnectionStringBuilder(connectionString).DataSource), name + ".txt");
    private static string Revision(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static ObjectDefinition ReadDefinition(string connectionString, string name)
    {
        lock (EditLock)
        {
            // Load validates the active export, name and database ownership.
            var script = Load(connectionString, name);
            var bytes = File.ReadAllBytes(ScriptPath(connectionString, name));
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
            string header = reader.ReadLine();
            if (header == null || !header.StartsWith(Header, StringComparison.Ordinal)) throw new IOException("Invalid script header.");
            var metadata = System.Text.Json.JsonSerializer.Deserialize<Metadata>(header.Substring(Header.Length));
            string database = new SQLiteConnectionStringBuilder(connectionString).DataSource;
            if (metadata == null || metadata.Error != null || !string.Equals(metadata.DatabaseFile,
                Path.GetFileName(database), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Script metadata changed or belongs to another database. Reopen the definition.");
            string sql = reader.ReadToEnd();
            if (sql != script.Sql) throw new IOException("Script changed while loading. Reopen the definition.");
            return new(sql, "Converted SQLite script. Saving edits the script file only; parameters, local initializers and conversion warnings are retained. " + string.Join(" | ", metadata.Warnings))
            { CanEdit = true, Version = Revision(bytes), EditRestriction = "" };
        }
    }

    internal static ObjectDefinition SaveDefinition(string connectionString, string name, ObjectDefinition original, string draft)
    {
        lock (EditLock)
        {
            SQLiteDefinitionValidator.Prepare(new("Procedure", "", name), draft);
            var current = ReadDefinition(connectionString, name);
            if (current.Version != original.Version || current.Sql != original.Sql)
                throw new InvalidOperationException("Script changed since it was opened. Reopen it before editing.");
            var script = Load(connectionString, name);
            var parameters = script.Metadata.Parameters.Concat(script.Metadata.Locals).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var statements = SQLiteDefinitionValidator.Statements(draft, out var texts);
            foreach (var token in statements.SelectMany(s => s))
                if (token[0] is '@' or ':' or '$' or '?')
                    if (!parameters.Contains(token) || token[0] != '@')
                        throw new ArgumentException("Unknown parameter or local variable: " + token + ". Edit only the SQL body using existing @parameters.");
            using (var connection = new SQLiteConnection(connectionString))
            {
                connection.Open();
                foreach (string sql in texts)
                {
                    using var command = new SQLiteCommand("EXPLAIN " + sql, connection) { CommandTimeout = 30 };
                    foreach (string parameter in parameters) command.Parameters.AddWithValue(parameter, DBNull.Value);
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) { }
                }
            }
            string path = ScriptPath(connectionString, name);
            var bytes = File.ReadAllBytes(path);
            if (Revision(bytes) != original.Version) throw new IOException("Script changed during validation. Reopen it.");
            // Preserve the metadata header exactly, including fields from newer converters.
            using var source = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
            string header = source.ReadLine();
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string backup = path + "." + Guid.NewGuid().ToString("N") + ".bak";
            try
            {
                byte[] updated = new UTF8Encoding(false).GetBytes(header + Environment.NewLine + draft);
                File.WriteAllBytes(temp, updated);
                if (ReadDefinition(connectionString, name).Version != original.Version) throw new IOException("Script changed before replacement. Reopen it.");
                File.Replace(temp, path, backup);
                return current with { Sql = draft, Version = Revision(updated) };
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
