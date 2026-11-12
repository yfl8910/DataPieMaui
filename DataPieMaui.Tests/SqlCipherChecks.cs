using System.Data;
using System.Data.Common;
using System.Text;
using DataPieCore;
using DBUtil;
using DataPieMaui.Services;
using Microsoft.Data.Sqlite;

internal static class SqlCipherChecks
{
    public static async Task Run(string root, Func<string, Func<Task>, Task> test)
    {
        string path = Path.Combine(root, "cipher.db");
        const string password = "  密码;'\"=test  ";
        string connectionString = new DBConfig { ProviderName = "SQLITE", DataBase = path, UserPwd = password }.GetConstring();
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        await test("SQLCipher opens encrypted files and rejects incorrect or missing passwords", async () =>
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Password = password, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA cipher_version";
                Check(Convert.ToString(command.ExecuteScalar())?.StartsWith("4.") == true, "SQLCipher 4 native library missing");
                command.CommandText = "CREATE TABLE Items(Id INTEGER PRIMARY KEY, Label TEXT UNIQUE, Amount INTEGER); INSERT INTO Items VALUES(1,'first',10); CREATE VIEW ItemView AS SELECT * FROM Items";
                command.ExecuteNonQuery();
            }
            Check(!Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 16).StartsWith("SQLite format"), "Fixture was not encrypted");
            var database = new DatabaseOperations();
            var info = new DatabaseConnectionInfo("SQLITE", connectionString, "cipher");
            await database.ConnectAsync(info);
            var table = database.Schema!.Tables.Single(t => t.Name == "Items");
            Check(!table.ColumnsLoaded && table.Columns.Count == 0, "Encrypted login loaded all columns");
            await database.EnsureColumnsAsync(table);
            Check(table.ColumnsLoaded && table.Columns.Count == 3, "Encrypted schema missing");
            foreach (string key in new[] { "wrong", "" })
            {
                string invalid = new DBConfig { ProviderName = "SQLITE", DataBase = path, UserPwd = key }.GetConstring();
                bool failed = false;
                try { await database.ConnectAsync(new DatabaseConnectionInfo("SQLITE", invalid, "invalid")); }
                catch (DbException) { failed = true; }
                Check(failed && database.Connection == info, "Invalid key accepted or previous connection lost");
            }
            string missing = Path.Combine(root, "missing-cipher.db");
            using var absent = DbAccessFactory.Create(new DBConfig { ProviderName = "SQLITE", DataBase = missing, UserPwd = password }.GetConstring(), "SQLITE");
            bool rejected = false;
            try { absent.conn.Open(); } catch (DbException) { rejected = true; }
            Check(rejected && !File.Exists(missing), "Connecting created an unintended encrypted file");
        });
        await test("SQLCipher supports queries, bulk import, editing, search and exports after reopening", () =>
        {
            using var access = (SQLiteDbAccess)DbAccessFactory.Create(connectionString, "SQLITE");
            using var data = access.GetDataTable("SELECT * FROM Items; SELECT COUNT(*) FROM Items");
            Check(data.Rows.Count == 1, "Data adapter failed");
            using var source = new DataTable(); source.Columns.Add("Label"); source.Columns.Add("Amount", typeof(long)); source.Rows.Add("second", 20L);
            using (var reader = source.CreateDataReader()) access.BulkInsert("Items", reader);
            var editor = TableEditorSession.Open(connectionString, "SQLITE", "main", "Items");
            Check(editor.TotalRows == 2, "Editor reopen failed");
            editor.Search("Label", "second");
            Check(editor.Data.Rows.Count == 1, "Parameterized search failed");
            editor.Save(editor.Data.Rows[0], new Dictionary<string, object> { ["Label"] = "changed" });
            editor.Search("Label", "changed");
            Check(editor.Data.Rows.Count == 1, "Editing did not persist");
            string export = Path.Combine(root, "cipher-export.xlsx"); editor.ExportExcel(export);
            Check(File.Exists(export) && new FileInfo(export).Length > 0, "Excel export failed");
            Check(SQLiteSqlServerExporter.Generate(connectionString).Sql.Contains("CREATE TABLE"), "SQL Server schema export failed");
            var view = new DefinitionObject("View", "main", "ItemView");
            var original = access.ReadDefinition(view);
            access.SaveDefinition(view, original, "CREATE VIEW ItemView AS SELECT Label FROM Items");
            Check(access.ReadDefinition(view).Sql.Contains("SELECT Label"), "Definition edit failed");
            editor.Delete(editor.Data.Rows.Cast<DataRow>().ToArray());
            editor.Search(null, ""); Check(editor.TotalRows == 1, "Delete failed");
            return Task.CompletedTask;
        });
        await test("SQLCipher analysis and parameterized procedure scripts retain credentials", () =>
        {
            var analysis = TableAnalysis.Open(connectionString, "SQLITE", "main", "Items");
            var request = new AnalysisRequest(new[] { "Label" }, new[] { new AnalysisCalculation("Amount", "SUM") });
            var result = analysis.Analyze(request);
            Check(result.RowCount == 1 && Convert.ToInt64(result.GetValue(0, 1)) == 10, "Encrypted analysis failed");
            using var access = (SQLiteDbAccess)DbAccessFactory.Create(connectionString, "SQLITE");
            using (var connection = SQLiteConnections.Create(connectionString))
            {
                connection.Open();
                var schema = new DbSchema
                {
                    Tables = new() { new TableStruct { Name = "Items", TableSchemaName = "dbo" } },
                    Procedures = new() { new Proc { Name = "SetAmount", SchemaName = "dbo", CreateSql = "CREATE PROCEDURE dbo.SetAmount @amount int AS BEGIN UPDATE dbo.Items SET Amount=@amount; END" } }
                };
                Check(SQLiteProcedureScripts.Export(connection, path, schema).Count == 0, "Encrypted script validation failed");
            }
            access.RunProcedure("SetAmount", new IDataParameter[] { new System.Data.SQLite.SQLiteParameter("@amount", 42) }, out _);
            using var values = access.GetDataTable("SELECT Amount FROM Items");
            Check(Convert.ToInt64(values.Rows[0][0]) == 42, "Procedure parameters failed across providers");
            var definition = new DefinitionObject("Procedure", "", "SetAmount");
            var original = access.ReadDefinition(definition);
            access.SaveDefinition(definition, original, original.Sql.Replace("Amount", "[Amount]"));
            return Task.CompletedTask;
        });
    }
}
