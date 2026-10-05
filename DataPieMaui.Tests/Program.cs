using System.Data;
using System.Data.SQLite;
using System.Text;
using DataPieCore;
using DataPieMaui.Services;
using DBUtil;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
string root = Path.Combine(Path.GetTempPath(), "DataPieMauiTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
async Task Test(string name, Func<Task> test)
{
    await test();
    Console.WriteLine("PASS " + name);
    passed++;
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
string Connection(string path) => new SQLiteConnectionStringBuilder { DataSource = path, FailIfMissing = true }.ToString();
void Sql(string path, string sql)
{
    using var connection = new SQLiteConnection(Connection(path)); connection.Open();
    using var command = new SQLiteCommand(sql, connection); command.ExecuteNonQuery();
}
long Count(string path, string table)
{
    using var connection = new SQLiteConnection(Connection(path)); connection.Open();
    using var command = new SQLiteCommand($"SELECT COUNT(*) FROM [{table}]", connection); return (long)command.ExecuteScalar();
}
try
{
    await Test("Streaming SQL Server schema preserves table identity and column metadata", () =>
    {
        using var tables = new DataTable();
        tables.Columns.Add("Name", typeof(string)); tables.Columns.Add("Schema", typeof(string)); tables.Columns.Add("Id", typeof(int));
        tables.Rows.Add("Items", "dbo", 10); tables.Rows.Add("Items", "archive", 20); tables.Rows.Add("Empty", "dbo", 30);
        using var columns = new DataTable();
        columns.Columns.Add("TableId", typeof(int)); columns.Columns.Add("Name", typeof(string)); columns.Columns.Add("Type", typeof(string));
        columns.Columns.Add("Length", typeof(short)); columns.Columns.Add("Default", typeof(string));
        columns.Columns.Add("Nullable", typeof(bool)); columns.Columns.Add("Identity", typeof(bool)); columns.Columns.Add("PrimaryKey", typeof(int));
        columns.Rows.Add(10, "Id", "int", (short)4, DBNull.Value, false, true, 1);
        columns.Rows.Add(10, "Name", "nvarchar", (short)100, "(N'')", true, false, 0);
        columns.Rows.Add(20, "Notes", "nvarchar", (short)-1, DBNull.Value, true, false, 0);
        using var reader = new DataTableReader(new[] { tables, columns });
        var result = SqlServerDbAccess.ReadTableSchema(reader);
        Check(result.Count == 3 && result[0].Columns.Count == 2 && result[1].Columns.Count == 1
            && result[2].Columns.Count == 0 && result[1].TableSchemaName == "archive", "Table/schema grouping changed");
        var id = result[0].Columns[0]; var name = result[0].Columns[1];
        Check(id.Name == "Id" && id.IsIdentity && id.IsPrimaryKey && !id.IsNullable && id.Default == "", "Key metadata changed");
        Check(name.Name == "Name" && name.Type == "nvarchar" && name.MaxLength == 100 && name.Default == "(N'')"
            && name.IsNullable && !name.IsIdentity && !name.IsPrimaryKey && result[1].Columns[0].MaxLength == -1, "Column order, length or default changed");
        using var emptyTables = tables.Clone(); using var emptyColumns = columns.Clone();
        using var emptyReader = new DataTableReader(new[] { emptyTables, emptyColumns });
        Check(SqlServerDbAccess.ReadTableSchema(emptyReader).Count == 0, "Empty schema failed");
        return Task.CompletedTask;
    });
    await Test("Import length validation reports file, data row, column and lengths", () =>
    {
        using var data = new DataTable();
        data.Columns.Add("Name"); data.Columns.Add("Unmapped"); data.Columns.Add("Notes");
        data.Rows.Add(DBNull.Value, "ignored long value", new string('x', 100));
        data.Rows.Add("中文", "", "");
        data.Rows.Add("😀", "", "");
        data.Rows.Add("中文😀", "", "");
        using var source = data.CreateDataReader();
        using var reader = new ImportLengthDataReader(source, new Dictionary<string, int> { ["Name"] = 2 }, "source.xlsx");
        Check(reader.Read() && reader.IsDBNull(0), "NULL or unmapped/MAX column rejected");
        Check(reader.Read() && reader.GetString(0) == "中文", "Exact boundary rejected");
        Check(reader.Read(), "Emoji UTF-16 boundary rejected");
        try { reader.Read(); throw new Exception("Expected length failure"); }
        catch (InvalidOperationException ex)
        {
            Check(ex.Message.Contains("source.xlsx") && ex.Message.Contains("数据行号：4")
                && ex.Message.Contains("列名：Name") && ex.Message.Contains("实际长度：4")
                && ex.Message.Contains("允许长度：2"), "Missing error context");
        }
        return Task.CompletedTask;
    });
    await Test("Result snapshot owns data and preserves typed sorting", () =>
    {
        using var source = new DataTable();
        source.Columns.Add("Number", typeof(int));
        source.Columns.Add("Date", typeof(DateTime));
        source.Columns.Add("Bytes", typeof(byte[]));
        byte[] bytes = [1, 2];
        source.Rows.Add(10, new DateTime(2026, 1, 1), bytes);
        source.Rows.Add(2, new DateTime(2025, 1, 1), new byte[] { 3 });
        source.Rows.Add(DBNull.Value, DBNull.Value, DBNull.Value);
        source.Rows.Add(99, DBNull.Value, DBNull.Value);
        QueryPreviewResult result;
        using (var reader = source.CreateDataReader()) result = QueryPreviewResult.Read(reader, 3);
        var snapshot = new QueryResultSnapshot(result, "Test database");
        source.Rows[0][0] = 99; bytes[0] = 9; source.Clear();
        Check(snapshot.RowCount == 3 && snapshot.FormatCell(0, 0) == "10", "Snapshot changed with source");
        Check(snapshot.FormatCell(0, 2) == "0102", "Binary snapshot was shared");
        Check(snapshot.Sort(0, false).SequenceEqual(new[] { 2, 1, 0 }), "Numeric/NULL ordering failed");
        Check(snapshot.Sort(1, true).SequenceEqual(new[] { 0, 1, 2 }), "Date ordering failed");
        Check(snapshot.Truncated && snapshot.Connection == "Test database", "Missing preview context");
        return Task.CompletedTask;
    });
    await Test("Result copy keeps selection, sort order and escaped cells", () =>
    {
        using var source = new DataTable();
        source.Columns.Add("Text"); source.Columns.Add("Nullable");
        source.Rows.Add("line\t\"quoted\"\nnext", DBNull.Value);
        source.Rows.Add("second", "value");
        using var reader = source.CreateDataReader();
        var snapshot = new QueryResultSnapshot(QueryPreviewResult.Read(reader, 1000), "Test");
        string selected = snapshot.CopyText(new[] { 0 });
        Check(selected.Contains("\"line\t\"\"quoted\"\"\nnext\"\tNULL") && !selected.Contains("second"), "Selected row escaping failed");
        string all = snapshot.CopyText(new[] { 1, 0 });
        Check(all.IndexOf("second", StringComparison.Ordinal) < all.IndexOf("quoted", StringComparison.Ordinal), "Copy order differs from display");
        var session = new QueryResultSession(); int changes = 0;
        session.Changed += () => changes++;
        session.Set(snapshot); session.Set(null);
        Check(changes == 2 && session.Current == null, "Result reset not propagated");
        return Task.CompletedTask;
    });
    var path = Path.Combine(root, "source.db"); SQLiteConnection.CreateFile(path);
    Sql(path, "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Name TEXT); INSERT INTO Items VALUES (1, '中文,quote'); INSERT INTO Items VALUES (2, 'second'); CREATE VIEW ItemNames AS SELECT Name FROM Items;");
    var operations = new DatabaseOperations();
    var info = new DatabaseConnectionInfo("SQLITE", Connection(path), "Fixture");
    await Test("SQLite native driver, connection and schema", async () =>
    {
        await operations.ConnectAsync(info);
        Check(operations.Schema!.Tables.Any(t => t.Name == "Items"), "Missing table");
        Check(operations.Schema.ViewNames.Contains("ItemNames"), "Missing view");
    });
    await Test("Failed connection preserves current session", async () =>
    {
        try { await operations.ConnectAsync(new("SQLITE", Connection(Path.Combine(root, "missing.db")), "Missing")); throw new Exception("Expected failure"); }
        catch (SQLiteException) { }
        Check(operations.Connection == info, "Failed switch replaced active session");
    });
    await Test("Bounded query preview and dynamic column names", () =>
    {
        using var access = operations.OpenAccess();
        var data = QueryPreview.Load(access, "SELECT Id AS duplicate, Name AS duplicate FROM Items ORDER BY Id", 1);
        Check(data.Truncated && data.RowCount == 1 && data.GetColumnName(0) != data.GetColumnName(1), "Preview contract failed");
        return Task.CompletedTask;
    });
    await Test("Direct preview preserves blobs after disposal and handles row boundaries", () =>
    {
        Sql(path, "CREATE TABLE Blobs (Id INTEGER, Payload BLOB); INSERT INTO Blobs VALUES (1, X'0102'), (2, X''), (3, NULL), (4, X'FF');");
        QueryPreviewResult result;
        using (var access = operations.OpenAccess())
        {
            result = QueryPreview.Load(access, "SELECT * FROM Blobs ORDER BY Id", 4);
            Check(!result.Truncated && result.RowCount == 4, "Exact limit marked truncated");
            var empty = QueryPreview.Load(access, "SELECT * FROM Blobs WHERE 1=0", 1);
            Check(empty.RowCount == 0 && empty.ColumnCount == 2 && !empty.Truncated, "Empty schema lost");
            var limited = QueryPreview.Load(access, "SELECT * FROM Blobs ORDER BY Id", 3);
            Check(limited.Truncated && limited.RowCount == 3, "Limit-plus-one failed");
        }
        Check(result.FormatCell(0, 1) == "0102" && result.FormatCell(3, 1) == "FF", "Binary changed after reader disposal");
        Check(result.FormatCell(1, 1) == "" && result.IsNull(2, 1), "Empty binary confused with NULL");
        var external = (byte[])result.GetValue(0, 1);
        external[0] = 99;
        Check(result.FormatCell(0, 1) == "0102", "Mutable binary backing storage escaped");
        return Task.CompletedTask;
    });
    var csv = Path.Combine(root, "items.csv"); var excel = Path.Combine(root, "items.xlsx");
    await Test("Full CSV export is independent of preview limit", () =>
    {
        using var access = operations.OpenAccess(); using var reader = access.GetDataReader("SELECT * FROM Items ORDER BY Id");
        CsvExporter.SaveCsv(reader, csv);
        var text = File.ReadAllText(csv, Encoding.GetEncoding("GB2312"));
        Check(text.Contains("中文,quote") && text.Contains("second") && text.Contains("\"中文,quote\""), "CSV export lost data");
        return Task.CompletedTask;
    });
    await Test("Excel export and CSV/XLSX imports", () =>
    {
        using (var access = operations.OpenAccess())
        using (var reader = access.GetDataReader("SELECT * FROM Items")) ExcelIO.SaveMiniExcel(excel, reader, "Items");
        var destination = Path.Combine(root, "destination.db"); SQLiteConnection.CreateFile(destination);
        Sql(destination, "CREATE TABLE Items (Id INTEGER, Name TEXT)");
        using var target = DbAccessFactory.Create(Connection(destination), "SQLITE");
        DatabaseOperations.Import(csv, "Items", target);
        DatabaseOperations.Import(excel, "Items", target);
        Check(Count(destination, "Items") == 4, "Import row count mismatch");
        DatabaseOperations.Import(path, "Items", target);
        Check(Count(destination, "Items") == 6, "SQLite file import failed");
        return Task.CompletedTask;
    });
    await Test("Multi-sheet MiniExcel and EPPlus export", () =>
    {
        using var access = operations.OpenAccess();
        foreach (bool epplus in new[] { false, true })
        {
            var output = Path.Combine(root, epplus ? "epplus.xlsx" : "multi.xlsx");
            if (epplus) ExcelIO.ExportSheetsWithEpplus(new[] { "Items", "ItemNames" }, output, access, "SQLITE");
            else ExcelIO.ExportSheetsWithMiniExcel(new[] { "Items", "ItemNames" }, output, access, "SQLITE");
            var sheets = MiniExcelLibs.MiniExcel.GetSheetNames(output);
            Check(sheets.SequenceEqual(new[] { "Items", "ItemNames" }), "Missing worksheet");
        }
        return Task.CompletedTask;
    });
    await Test("Connection CRUD and duplicate prevention", () =>
    {
        var destination = new ConnectionStore(Path.Combine(root, "connections.db"));
        var record = new Dbinfo { Dbname = "Fixture", Dbtype = "SQLITE", ConnectionStrings = Connection(path) };
        destination.Save(record, true); destination.Save(record, true);
        var records = destination.Load(); Check(records.Count == 1, "Duplicate saved connection");
        Check(records[0].ConnectionStrings == record.ConnectionStrings, "Connection string changed");
        records[0].Dbname = "Renamed"; destination.Save(records[0]);
        Check(destination.Load().Single().Dbname == "Renamed", "Update failed");
        destination.Delete(records[0].Id); Check(destination.Load().Count == 0, "Delete failed");
        return Task.CompletedTask;
    });
    await Test("SQL Server authentication string construction", () =>
    {
        var config = new DBConfig { ProviderName = "SQLSERVER", ServerName = "localhost", DataBase = "sample", ValidataType = "Windows" };
        var integrated = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(config.GetConstring());
        Check(integrated.IntegratedSecurity && integrated.InitialCatalog == "sample", "Windows auth failed");
        Check(integrated.Encrypt == Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory && integrated.TrustServerCertificate, "Secure connection defaults changed");
        foreach (var value in new[] { config.GetConstring(), config.GetSQLmasterConstring() })
        {
            var trusted = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(value);
            Check(trusted.TrustServerCertificate && trusted.Encrypt == Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory, "Certificate option not applied to database and master connections");
        }
        const string explicitSettings = "Server=localhost;Database=sample;Integrated Security=true;Encrypt=Optional;TrustServerCertificate=false";
        var savedConnection = new DatabaseConnectionInfo("SQLSERVER", explicitSettings, "saved");
        Check(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(savedConnection.ConnectionString).TrustServerCertificate, "Saved connection did not trust certificate by default");
        config.ValidataType = "SQL Server"; config.UserName = "test"; config.UserPwd = "a;b=\"c";
        var sqlAuth = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(config.GetConstring());
        Check(!sqlAuth.IntegratedSecurity && sqlAuth.Password == config.UserPwd, "Password escaping failed");
        return Task.CompletedTask;
    });
    await Test("Folder import includes only supported batch formats", () =>
    {
        File.WriteAllText(Path.Combine(root, "ignored.txt"), "ignore");
        var files = ImportFiles.GetFiles(root);
        Check(files.Any(f => f.Name == "items.csv") && files.All(f => f.Extension is ".csv" or ".xlsx"), "Batch filter changed");
        return Task.CompletedTask;
    });
    await Test("SQLite procedure conversion and execution", () =>
    {
        var converted = Path.Combine(root, "converted.db");
        SqlServerToSQLite._cancelled = false;
        SqlServerToSQLite.dbs = new DbSchema
        {
            Name = "Converted",
            Tables = new() { new TableStruct { Name = "Items", TableSchemaName = "dbo", Columns = new() { new Column { Name = "Id", Type = "int", Default = "", IsPrimaryKey = true }, new Column { Name = "Name", Type = "nvarchar", Default = "", MaxLength = 100, IsNullable = true } } } },
            Procedures = new() { new Proc { Name = "AddItem", SchemaName = "dbo", CreateSql = "CREATE PROCEDURE dbo.AddItem AS BEGIN INSERT INTO dbo.Items (Id, Name) VALUES (1, N'created'); END" } }
        };
        var result = SqlServerToSQLite.CreateSQLiteDatabase(converted, null, true);
        Check(result.ProcedureErrors.Count == 0, "Procedure conversion warnings: " + string.Join(";", result.ProcedureErrors));
        using var access = DbAccessFactory.Create(Connection(converted), "SQLITE");
        var proc = access.GetProcs().Single();
        var procedureObject = ((IDefinitionReader)access).ListDefinitionObjects().Single(o => o.Kind == "Procedure");
        var detail = ((IDefinitionReader)access).ReadDefinition(procedureObject);
        Check(detail.Sql.Contains("INSERT") && detail.Notice.Contains("Converted SQLite"), "Procedure script detail missing");
        Check(Count(converted, "Items") == 0, "Browsing executed a procedure");
        access.RunProcedure(proc.Name);
        Check(Count(converted, "Items") == 1, "Procedure did not insert");
        return Task.CompletedTask;
    });
    await Test("Definitions are read on demand and preserve special names", () =>
    {
        var schemaPath = Path.Combine(root, "schema.db"); SQLiteConnection.CreateFile(schemaPath);
        Sql(schemaPath, "CREATE TABLE \"Odd'Name\" (Id INTEGER PRIMARY KEY, Amount DECIMAL(12,3) NOT NULL DEFAULT 0, Doubled AS (Amount*2) VIRTUAL); CREATE VIEW \"View'Name\" AS SELECT '<script>bad</script>' AS Text FROM \"Odd'Name\";");
        using var access = DbAccessFactory.Create(Connection(schemaPath), "SQLITE");
        var initialSchema = access.ShowDbSchema();
        Check(initialSchema.ViewDefinitions.Count == 0 && initialSchema.Procedures.All(p => string.IsNullOrEmpty(p.CreateSql)), "Initialization loaded definitions");
        var objects = initialSchema.DefinitionObjects;
        var view = objects.Single(o => o.Kind == "View");
        Check(((IDefinitionReader)access).ReadDefinition(view).Sql.Contains("<script>bad</script>"), "View definition changed");
        Sql(schemaPath, "DROP VIEW \"View'Name\"");
        try { ((IDefinitionReader)access).ReadDefinition(view); throw new Exception("Missing object accepted"); }
        catch (InvalidOperationException) { }
        return Task.CompletedTask;
    });
    await Test("Conversion cancellation remains effective", () =>
    {
        SqlServerToSQLite._cancelled = true;
        try { SqlServerToSQLite.CreateSQLiteDatabase(Path.Combine(root, "cancelled.db"), null, true); throw new Exception("Expected cancellation"); }
        catch (ApplicationException ex) when (ex.Message == "User cancelled the conversion") { }
        finally { SqlServerToSQLite._cancelled = false; }
        return Task.CompletedTask;
    });
    await DefinitionEditChecks.Run(Test);
    await DialogLifecycleChecks.Run(Test);
    await ProcedureExecutionChecks.Run(Test);
    await SQLiteDefinitionEditChecks.Run(root, Test);
    await WorkspaceChecks.Run(root, Test, args.Contains("--render-ui"));
    Console.WriteLine($"{passed} regression checks passed.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
finally
{
    SQLiteConnection.ClearAllPools();
    Directory.Delete(root, recursive: true);
}
