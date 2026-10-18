using System.Data;
using DataPieCore;
using DBUtil;
using static DataPieCore.SqlServerScriptExporter;

internal static class SqlServerScriptChecks
{
    public static async Task Run(string root, Func<string, Func<Task>, Task> test)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        await test("SQL Server script preserves table types, identity, computed fields, defaults and ordered primary keys", () =>
        {
            var table = new ScriptTable(1, "sales'odd]", "Orders]", new()
            {
                new("A", "bigint", 8, 19, 0, false, IdentitySeed: "100", IdentityIncrement: "5", IdentityNotForReplication: true),
                new("B", "int", 4, 10, 0, false),
                new("Name", "nvarchar", 100, 0, 0, true, Collation: "Latin1_General_100_CI_AS", DefaultName: "DF_Name]", DefaultSql: "(N'hello')"),
                new("Amount", "decimal", 17, 18, 4, true),
                new("Created", "datetime2", 8, 0, 3, true, DefaultName: "DF_Created", DefaultSql: "(sysdatetime())"),
                new("Calculated", "int", 4, 10, 0, false, Computed: "([B]+1)", Persisted: true)
            }, new PrimaryKey("PK_Orders]", false, new[] { ("B", true), ("A", false) }));
            var result = Generate(new Snapshot("Source", new() { "sales'odd]" }, new() { table }, new(), new(), new()));
            Check(result.Tables == 1 && result.Views == 0 && result.Warnings.Count == 0, "Wrong export counts");
            Check(result.Sql.Contains("CREATE TABLE [sales'odd]]].[Orders]]]") && result.Sql.Contains("SCHEMA_ID(N'sales''odd]')"), "Identifiers/literals not escaped");
            Check(result.Sql.Contains("IDENTITY(100,5) NOT FOR REPLICATION") && result.Sql.Contains("nvarchar(50) COLLATE [Latin1_General_100_CI_AS]"), "Identity or Unicode length/collation lost");
            Check(result.Sql.Contains("decimal(18,4)") && result.Sql.Contains("datetime2(3)"), "Type precision/scale lost");
            Check(result.Sql.Contains("CONSTRAINT [DF_Name]]] DEFAULT (N'hello')") && result.Sql.Contains("PRIMARY KEY NONCLUSTERED ([B] DESC, [A] ASC)"), "Defaults or composite key metadata lost");
            Check(result.Sql.Contains("[Calculated] AS ([B]+1) PERSISTED NOT NULL"), "Computed expression lost");
            Check(!result.Sql.Contains("CREATE INDEX") && !result.Sql.Contains("DROP TABLE") && !result.Sql.Contains("INSERT INTO"), "Out-of-scope statements generated");
            return Task.CompletedTask;
        });
        await test("SQL Server script omits database-default collation and preserves column overrides", () =>
        {
            var table = new ScriptTable(1, "dbo", "Names", new()
            {
                new("DefaultName", "nvarchar", 100, 0, 0, true, Collation: "Chinese_PRC_CI_AS"),
                new("CaseName", "varchar", 50, 0, 0, true, Collation: "chinese_prc_ci_as"),
                new("SpecialName", "nvarchar", 100, 0, 0, true, Collation: "Latin1_General_100_CS_AS")
            });
            var result = Generate(new Snapshot("Source", new(), new() { table }, new(), new(), new(), "Chinese_PRC_CI_AS"));
            Check(result.Sql.Contains("[DefaultName] nvarchar(50) NULL") && result.Sql.Contains("[CaseName] varchar(50) NULL"), "Database-default collation should be inherited");
            Check(result.Sql.Contains("[SpecialName] nvarchar(50) COLLATE [Latin1_General_100_CS_AS] NULL"), "Non-default column collation lost");
            return Task.CompletedTask;
        });
        await test("SQL Server script normalizes module declarations without modifying bodies and orders dependencies", () =>
        {
            var table = new ScriptTable(1, "dbo", "ZTable", new() { new("Id", "int", 4, 10, 0, false) });
            var modules = new List<ScriptModule>
            {
                new(2, "dbo", "AView", "View", "/* saved comment */ ALTER VIEW OldName AS SELECT Id FROM dbo.ZTable", false, true),
                new(3, "dbo", "BProc", "Procedure", "CREATE /* comment */ OR ALTER PROC dbo.OldProc AS SELECT N'ALTER PROC OldProc', Id FROM dbo.AView;", true, false)
            };
            var result = Generate(new Snapshot("Source", new() { "dbo" }, new() { table }, modules,
                new() { new(2, 1, "dbo.ZTable"), new(3, 2, "dbo.AView") }, new()));
            Check(result.Sql.IndexOf("CREATE TABLE [dbo].[ZTable]", StringComparison.Ordinal) < result.Sql.IndexOf("CREATE VIEW [dbo].[AView]", StringComparison.Ordinal), "View appears before table dependency");
            Check(result.Sql.Contains("SET QUOTED_IDENTIFIER OFF\nGO") && result.Sql.Contains("SET ANSI_NULLS OFF\nGO"), "Persisted SET options lost");
            Check(result.Sql.Contains("N'ALTER PROC OldProc'") && result.Sql.Contains("PROC [dbo].[BProc] AS"), "Declaration normalization modified body or kept stale name");
            Check(result.Views == 1 && result.Procedures == 1 && result.Warnings.Count == 0, "Wrong module counts");
            Check(DefinitionSqlValidator.PrepareCreateForExport(new("View", "dbo", "Names"), "CREATE VIEW Names AS SELECT 1 AS Value", true).Contains("CREATE VIEW [dbo].[Names]"), "Unqualified declaration not normalized");
            return Task.CompletedTask;
        });
        await test("SQL Server script reports skipped objects, excluded dependencies and cycles", () =>
        {
            var modules = new List<ScriptModule>
            {
                new(2, "dbo", "Encrypted", "Procedure", null, true, true),
                new(3, "dbo", "First", "Procedure", "CREATE PROC dbo.First AS EXEC dbo.Second", true, true),
                new(4, "dbo", "Second", "Procedure", "CREATE PROC dbo.Second AS EXEC dbo.First", true, true)
            };
            var result = Generate(new Snapshot("Source\nGO", new(), new() { new(1, "dbo", "Special", new(), Unsupported: "memory optimized") }, modules,
                new() { new(3, 4, "dbo.Second"), new(4, 3, "dbo.First"), new(3, 999, "dbo.ExcludedFunction"), new(4, null, "OtherDatabase.dbo.Object") }, new()));
            Check(result.Tables == 0 && result.Procedures == 2 && result.Warnings.Any(w => w.Contains("Encrypted")), "Skipped objects counted as exported");
            Check(result.Warnings.Any(w => w.Contains("Cyclic dependencies")) && result.Warnings.Any(w => w.Contains("ExcludedFunction")) && result.Warnings.Any(w => w.Contains("OtherDatabase")), "Dependency warnings missing");
            Check(result.Sql.Contains("-- Source database: Source GO") && !result.Sql.Contains("CREATE TABLE [dbo].[Special]"), "Comment escaping or skipped table failed");
            return Task.CompletedTask;
        });
        await test("SQL Server script metadata reader preserves result-set ordinals and writes one complete UTF-16 LE file with BOM", () =>
        {
            using var databaseCollation = Table(typeof(string)); databaseCollation.Rows.Add("Chinese_PRC_CI_AS");
            using var schemas = Table(typeof(string)); schemas.Rows.Add("dbo");
            using var tables = Table(typeof(int), typeof(string), typeof(string), typeof(bool)); tables.Rows.Add(1, "dbo", "Items", false);
            using var columns = Table(typeof(int), typeof(string), typeof(string), typeof(short), typeof(byte), typeof(byte), typeof(bool), typeof(string), typeof(string), typeof(string), typeof(bool), typeof(string), typeof(bool), typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(int), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(string));
            columns.Rows.Add(1, "Id", "bigint", (short)8, (byte)19, (byte)0, false, DBNull.Value, "10", "2", true, DBNull.Value, false, DBNull.Value, DBNull.Value, false, false, 1, true, false, false, false, false, "bigint");
            using var keys = Table(typeof(int), typeof(string), typeof(int), typeof(string), typeof(bool)); keys.Rows.Add(1, "PK_Items", 1, "Id", false);
            using var modules = Table(typeof(int), typeof(string), typeof(string), typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(bool));
            using var dependencies = Table(typeof(int), typeof(int), typeof(string), typeof(int));
            using var warnings = Table(typeof(string));
            using var reader = new DataTableReader(new[] { databaseCollation, schemas, tables, columns, keys, modules, dependencies, warnings });
            var snapshot = ReadSnapshot("数据库", reader);
            Check(snapshot.DatabaseCollation == "Chinese_PRC_CI_AS", "Database collation metadata lost");
            var result = Generate(snapshot);
            Check(result.Sql.Contains("IDENTITY(10,2) NOT FOR REPLICATION") && result.Sql.Contains("PRIMARY KEY CLUSTERED ([Id] ASC)"), "Reader ordinal mapping incorrect");
            string file = Path.Combine(root, "object-script.sql"); File.WriteAllText(file, "old content"); WriteFile(file, result.Sql);
            byte[] bytes = File.ReadAllBytes(file);
            Check(bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE
                && System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2) == result.Sql, "Expected UTF-16 LE BOM and intact Unicode content");
            Check(File.ReadAllText(file) == result.Sql && Directory.GetFiles(root, "object-script.sql.*.tmp").Length == 0, "Atomic output left partial content or temporary file");
            using (var locked = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { WriteFile(file, "replacement"); throw new Exception("Expected locked destination failure"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Check(File.ReadAllText(file) == result.Sql && Directory.GetFiles(root, "object-script.sql.*.tmp").Length == 0, "Failed replacement damaged prior script or left partial output");
            return Task.CompletedTask;
        });
    }
    private static DataTable Table(params Type[] types)
    {
        var table = new DataTable();
        for (int i = 0; i < types.Length; i++) table.Columns.Add("C" + i, types[i]);
        return table;
    }
}
