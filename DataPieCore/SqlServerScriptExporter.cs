using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using DBUtil;
using Microsoft.Data.SqlClient;

namespace DataPieCore;

public sealed record SqlServerScriptResult(string Sql, int Tables, int Views, int Procedures, IReadOnlyList<string> Warnings);

public static class SqlServerScriptExporter
{
    internal sealed record ScriptColumn(string Name, string Type, int Length, int Precision, int Scale, bool Nullable,
        string Collation = null, string IdentitySeed = null, string IdentityIncrement = null, bool IdentityNotForReplication = false,
        string Computed = null, bool Persisted = false, string DefaultName = null, string DefaultSql = null, bool RowGuid = false, bool Sparse = false);
    internal sealed record PrimaryKey(string Name, bool Clustered, IReadOnlyList<(string Name, bool Descending)> Columns);
    internal sealed record ScriptTable(int Id, string Schema, string Name, List<ScriptColumn> Columns, PrimaryKey Key = null, string Unsupported = null);
    internal sealed record ScriptModule(int Id, string Schema, string Name, string Kind, string Sql, bool QuotedIdentifier, bool AnsiNulls, bool Unsupported = false);
    internal sealed record Dependency(int From, int? To, string Name, bool ObjectReference = true);
    internal sealed record Snapshot(string Database, List<string> Schemas, List<ScriptTable> Tables, List<ScriptModule> Modules,
        List<Dependency> Dependencies, List<string> Warnings, string DatabaseCollation = null);

    public static SqlServerScriptResult Generate(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using (var check = new SqlCommand("SELECT CONVERT(int,SERVERPROPERTY('ProductMajorVersion')),HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','VIEW DEFINITION')", connection))
        using (var reader = check.ExecuteReader())
        {
            reader.Read();
            if (reader.GetInt32(0) < 13) throw new NotSupportedException("SQL Server script export requires SQL Server 2016 or later.");
            if (reader.IsDBNull(1) || reader.GetInt32(1) != 1) throw new InvalidOperationException("Database VIEW DEFINITION permission is required to export the database object list reliably.");
        }
        using var command = new SqlCommand(MetadataSql, connection) { CommandTimeout = 120 };
        using var data = command.ExecuteReader();
        return Generate(ReadSnapshot(connection.Database, data));
    }

    public static SqlServerScriptResult Export(string connectionString, string path)
    {
        var result = Generate(connectionString);
        WriteFile(path, result.Sql);
        return result;
    }
    internal static void WriteFile(string path, string sql)
    {
        path = Path.GetFullPath(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, sql, Encoding.Unicode);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Q(string name) => SqlQueryBuilder.QuoteIdentifier(name, "SQLSERVER");
    private static string Qualified(string schema, string name) => Q(schema) + "." + Q(name);
    private static string Comment(string text) => text.Replace("\r", " ").Replace("\n", " ");
    internal static SqlServerScriptResult Generate(Snapshot snapshot)
    {
        var warnings = new List<string>(snapshot.Warnings);
        var scripts = new Dictionary<int, string>();
        var names = new Dictionary<int, string>();
        int tableCount = 0, viewCount = 0, procedureCount = 0;
        foreach (var table in snapshot.Tables)
        {
            string name = Qualified(table.Schema, table.Name);
            if (!string.IsNullOrEmpty(table.Unsupported) || table.Columns.Count == 0)
            { warnings.Add(name + ": table skipped: " + (table.Unsupported ?? "column metadata unavailable")); continue; }
            try
            {
                var columns = table.Columns.Select(column => ColumnSql(column, snapshot.DatabaseCollation)).ToList();
                if (table.Key is { } key)
                {
                    if (key.Columns.Count == 0 || key.Columns.Any(k => !table.Columns.Any(c => c.Name == k.Name))) throw new InvalidOperationException("primary key metadata is incomplete");
                    columns.Add("    CONSTRAINT " + Q(key.Name) + " PRIMARY KEY " + (key.Clustered ? "CLUSTERED" : "NONCLUSTERED")
                        + " (" + string.Join(", ", key.Columns.Select(k => Q(k.Name) + (k.Descending ? " DESC" : " ASC"))) + ")");
                }
                scripts.Add(table.Id, "SET ANSI_NULLS ON\nGO\nSET QUOTED_IDENTIFIER ON\nGO\nSET ANSI_PADDING ON\nGO\nCREATE TABLE " + name + " (\n" + string.Join(",\n", columns) + "\n);\nGO\n");
                names[table.Id] = name; tableCount++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            { warnings.Add(name + ": table skipped: " + ex.Message); }
        }
        foreach (var module in snapshot.Modules)
        {
            string name = Qualified(module.Schema, module.Name);
            if (module.Unsupported || string.IsNullOrWhiteSpace(module.Sql))
            { warnings.Add(name + ": " + module.Kind + " skipped: encrypted, inaccessible, CLR or natively compiled definition."); continue; }
            try
            {
                string sql = DefinitionSqlValidator.PrepareCreateForExport(new(module.Kind, module.Schema, module.Name), module.Sql, module.QuotedIdentifier);
                scripts.Add(module.Id, "SET ANSI_NULLS " + (module.AnsiNulls ? "ON" : "OFF") + "\nGO\nSET QUOTED_IDENTIFIER " + (module.QuotedIdentifier ? "ON" : "OFF") + "\nGO\n" + sql.TrimEnd() + "\nGO\n");
                names[module.Id] = name;
                if (module.Kind == "View") viewCount++; else procedureCount++;
            }
            catch (ArgumentException ex) { warnings.Add(name + ": definition skipped: " + ex.Message); }
        }
        foreach (var dependency in snapshot.Dependencies.Where(d => scripts.ContainsKey(d.From)))
            if (!dependency.ObjectReference || dependency.To == null || !scripts.ContainsKey(dependency.To.Value))
                warnings.Add(names[dependency.From] + ": dependency not included or unresolved: " + dependency.Name + ". Create it separately if required.");

        // Topological ordering covers included table/module dependencies, not only views.
        var remaining = scripts.Keys.ToHashSet();
        var output = new StringBuilder();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(id => !snapshot.Dependencies.Any(d => d.From == id && d.ObjectReference && d.To.HasValue && d.To.Value != id && remaining.Contains(d.To.Value)))
                .OrderBy(id => names[id], StringComparer.Ordinal).ToArray();
            if (ready.Length == 0)
            {
                ready = remaining.OrderBy(id => names[id], StringComparer.Ordinal).ToArray();
                warnings.Add("Cyclic dependencies: " + string.Join(", ", ready.Select(id => names[id])) + ". Creation order requires manual review.");
            }
            foreach (int id in ready) { output.AppendLine(scripts[id]); remaining.Remove(id); }
        }
        warnings = warnings.Distinct(StringComparer.Ordinal).ToList();
        var header = new StringBuilder("-- SQL Server object creation script\n");
        header.AppendLine("-- Source database: " + Comment(snapshot.Database));
        header.AppendLine("-- Includes schemas, supported tables, primary keys, defaults, views and procedures. No data or DROP statements are generated.");
        header.AppendLine("-- Excludes foreign keys, independent UNIQUE/CHECK constraints, non-primary indexes, functions, triggers, types, sequences, users and permissions.");
        header.AppendLine("-- Execute in the intended EMPTY database. GO requires a batch-aware client (for example SSMS). Re-export if source DDL changes during export.");
        foreach (string warning in warnings) header.AppendLine("-- WARNING: " + Comment(warning));
        header.AppendLine("SET ANSI_NULLS ON;\nSET QUOTED_IDENTIFIER ON;\nSET ANSI_PADDING ON;\nSET ANSI_WARNINGS ON;\nSET ARITHABORT ON;\nSET CONCAT_NULL_YIELDS_NULL ON;\nSET NUMERIC_ROUNDABORT OFF;\nGO\n");
        foreach (string schema in snapshot.Schemas.Where(s => s != "dbo" && s != "sys" && s != "INFORMATION_SCHEMA").Distinct().OrderBy(s => s, StringComparer.Ordinal))
        {
            string statement = "CREATE SCHEMA " + Q(schema) + " AUTHORIZATION [dbo]";
            header.AppendLine("IF SCHEMA_ID(N'" + schema.Replace("'", "''") + "') IS NULL EXEC(N'" + statement.Replace("'", "''") + "');\nGO\n");
        }
        return new(header.Append(output).ToString(), tableCount, viewCount, procedureCount, warnings);
    }
    private static string ColumnSql(ScriptColumn column, string databaseCollation)
    {
        string sql = "    " + Q(column.Name);
        if (column.Computed != null) return sql + " AS " + column.Computed + (column.Persisted ? " PERSISTED" + (column.Nullable ? "" : " NOT NULL") : "");
        if (string.IsNullOrEmpty(column.Type)) throw new InvalidOperationException("column type unavailable: " + column.Name);
        string type = column.Type.ToLowerInvariant();
        var info = new Column { Type = type, MaxLength = column.Length, Precision = column.Precision, Scale = column.Scale };
        sql += " " + info.DisplayType("SQLSERVER");
        if (column.Collation != null && !string.Equals(column.Collation, databaseCollation, StringComparison.OrdinalIgnoreCase)
            && type is "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "sysname") sql += " COLLATE " + Q(column.Collation);
        if (column.IdentitySeed != null)
            sql += " IDENTITY(" + column.IdentitySeed + "," + column.IdentityIncrement + ")" + (column.IdentityNotForReplication ? " NOT FOR REPLICATION" : "");
        if (column.RowGuid) sql += " ROWGUIDCOL";
        if (column.Sparse) sql += " SPARSE";
        sql += column.Nullable ? " NULL" : " NOT NULL";
        if (column.DefaultSql != null)
        {
            if (column.DefaultName == null) throw new InvalidOperationException("default constraint name unavailable: " + column.Name);
            sql += " CONSTRAINT " + Q(column.DefaultName) + " DEFAULT " + column.DefaultSql;
        }
        return sql;
    }

    internal static Snapshot ReadSnapshot(string database, IDataReader reader)
    {
        if (!reader.Read() || reader.IsDBNull(0)) throw new InvalidOperationException("Database collation metadata unavailable.");
        string databaseCollation = reader.GetString(0);
        Next(reader);
        var schemas = new List<string>();
        while (reader.Read()) schemas.Add(reader.GetString(0));
        Next(reader);
        var tables = new Dictionary<int, ScriptTable>();
        while (reader.Read()) tables.Add(reader.GetInt32(0), new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), new(), Unsupported: reader.GetBoolean(3) ? "special table features (temporal, memory-optimized or FileTable)" : null));
        Next(reader);
        var warnings = new List<string>();
        while (reader.Read())
        {
            int id = reader.GetInt32(0);
            if (!tables.TryGetValue(id, out var table)) continue;
            string Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            bool Flag(int i) => !reader.IsDBNull(i) && reader.GetBoolean(i);
            if (Flag(21)) tables[id] = table = table with { Unsupported = "unsupported CLR/typed XML/encrypted/hidden/generated/FILESTREAM/column-set or bound-default column " + Q(reader.GetString(1)) };
            if (Flag(18) && (Text(8) == null || Text(9) == null) || Text(13) != null && Text(14) == null)
                tables[id] = table = table with { Unsupported = "identity or default metadata unavailable" };
            if (Flag(22)) warnings.Add(Qualified(table.Schema, table.Name) + "." + Q(reader.GetString(1)) + ": alias type " + Text(23) + " expanded to its base type; the alias type itself is not exported.");
            table.Columns.Add(new(reader.GetString(1), Text(2), reader.GetInt16(3), reader.GetByte(4), reader.GetByte(5), Flag(6), Text(7), Text(8), Text(9), Flag(10), Text(11), Flag(12), Text(13), Text(14), Flag(15), Flag(16)));
            if (Flag(20)) warnings.Add(Qualified(table.Schema, table.Name) + ": source ANSI_PADDING OFF column is scripted with ANSI_PADDING ON.");
        }
        Next(reader);
        while (reader.Read())
        {
            int id = reader.GetInt32(0);
            if (!tables.TryGetValue(id, out var table)) continue;
            var key = table.Key ?? new PrimaryKey(reader.GetString(1), reader.GetInt32(2) == 1, new List<(string, bool)>());
            ((List<(string, bool)>)key.Columns).Add((reader.GetString(3), reader.GetBoolean(4)));
            tables[id] = table with { Key = key };
        }
        Next(reader);
        var modules = new List<ScriptModule>();
        while (reader.Read())
            modules.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), !reader.IsDBNull(5) && reader.GetBoolean(5), !reader.IsDBNull(6) && reader.GetBoolean(6), reader.GetBoolean(7)));
        Next(reader);
        var dependencies = new List<Dependency>();
        while (reader.Read()) dependencies.Add(new(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3) == 1));
        Next(reader);
        while (reader.Read()) warnings.Add(reader.GetString(0));
        return new(database, schemas, tables.Values.OrderBy(t => t.Schema).ThenBy(t => t.Name).ToList(), modules, dependencies, warnings, databaseCollation);
    }
    private static void Next(IDataReader reader) { if (!reader.NextResult()) throw new InvalidOperationException("Incomplete SQL Server script metadata."); }

    private const string MetadataSql = """
        SELECT CONVERT(nvarchar(128),DATABASEPROPERTYEX(DB_NAME(),'Collation'));
        SELECT s.name FROM sys.schemas s WHERE s.name NOT IN ('sys','INFORMATION_SCHEMA','guest','db_owner','db_accessadmin','db_securityadmin','db_ddladmin','db_backupoperator','db_datareader','db_datawriter','db_denydatareader','db_denydatawriter') ORDER BY s.name;
        SELECT t.object_id,s.name,t.name,CONVERT(bit,CASE WHEN t.temporal_type<>0 OR t.is_memory_optimized=1 OR t.is_filetable=1 THEN 1 ELSE 0 END)
        FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0 ORDER BY s.name,t.name;
        SELECT c.object_id,c.name,CASE WHEN ty.is_user_defined=1 AND ty.is_assembly_type=0 THEN base.name ELSE ty.name END,
        c.max_length,c.precision,c.scale,c.is_nullable,c.collation_name,
        CONVERT(nvarchar(100),ic.seed_value),CONVERT(nvarchar(100),ic.increment_value),ic.is_not_for_replication,
        cc.definition,cc.is_persisted,dc.name,dc.definition,c.is_rowguidcol,c.is_sparse,
        c.column_id,c.is_identity,c.is_computed,
        CONVERT(bit,CASE WHEN c.is_ansi_padded=0 AND c.system_type_id IN (165,167) THEN 1 ELSE 0 END),
        CONVERT(bit,CASE WHEN (ty.is_assembly_type=1 AND ty.is_user_defined=1) OR c.xml_collection_id<>0 OR c.encryption_type IS NOT NULL
          OR c.is_hidden=1 OR c.generated_always_type<>0 OR c.is_filestream=1 OR c.is_column_set=1
          OR (c.default_object_id<>0 AND dc.object_id IS NULL) OR (c.is_computed=1 AND cc.definition IS NULL) THEN 1 ELSE 0 END),
        CONVERT(bit,CASE WHEN ty.is_user_defined=1 AND ty.is_assembly_type=0 THEN 1 ELSE 0 END),ty.name
        FROM sys.tables t JOIN sys.columns c ON c.object_id=t.object_id
        JOIN sys.types ty ON ty.user_type_id=c.user_type_id
        LEFT JOIN sys.types base ON base.user_type_id=c.system_type_id AND base.user_type_id=base.system_type_id
        LEFT JOIN sys.identity_columns ic ON ic.object_id=c.object_id AND ic.column_id=c.column_id
        LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
        LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
        WHERE t.is_ms_shipped=0 ORDER BY c.object_id,c.column_id;
        SELECT kc.parent_object_id,kc.name,CONVERT(int,i.type),c.name,ic.is_descending_key
        FROM sys.key_constraints kc JOIN sys.indexes i ON i.object_id=kc.parent_object_id AND i.index_id=kc.unique_index_id
        JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0
        JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
        WHERE kc.type='PK' ORDER BY kc.parent_object_id,ic.key_ordinal;
        SELECT o.object_id,s.name,o.name,CASE WHEN o.type='V' THEN 'View' ELSE 'Procedure' END,m.definition,m.uses_quoted_identifier,m.uses_ansi_nulls,
        CONVERT(bit,CASE WHEN o.type='PC' OR m.uses_native_compilation=1 THEN 1 ELSE 0 END)
        FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id LEFT JOIN sys.sql_modules m ON m.object_id=o.object_id
        WHERE o.type IN ('V','P','PC') AND o.is_ms_shipped=0 ORDER BY s.name,o.name;
        SELECT COALESCE(dc.parent_object_id,d.referencing_id),d.referenced_id,
        CONCAT(CASE WHEN d.referenced_server_name IS NOT NULL THEN d.referenced_server_name+'.' ELSE '' END,
          CASE WHEN d.referenced_database_name IS NOT NULL THEN d.referenced_database_name+'.' ELSE '' END,
          COALESCE(d.referenced_schema_name+'.',''),COALESCE(d.referenced_entity_name,'unknown')),
        CONVERT(int,d.referenced_class)
        FROM sys.sql_expression_dependencies d LEFT JOIN sys.default_constraints dc ON dc.object_id=d.referencing_id;
        SELECT CONCAT(s.name,'.',o.name,': EXECUTE AS principals and/or signatures are not exported; review security dependencies.')
        FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id JOIN sys.sql_modules m ON m.object_id=o.object_id
        WHERE o.is_ms_shipped=0 AND o.type IN ('V','P') AND (m.execute_as_principal_id IS NOT NULL OR EXISTS(SELECT 1 FROM sys.crypt_properties p WHERE p.major_id=o.object_id AND p.class=1));
        """;
}
