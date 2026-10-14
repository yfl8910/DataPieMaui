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
    await Test("SQL Server import normalizes numeric/date blanks without changing text", () =>
    {
        using var data = new DataTable();
        foreach (var name in new[] { "Additional Expense", "Created", "Label", "Mixed", "Optional" }) data.Columns.Add(name, typeof(object));
        data.Rows.Add(12.5, new DateTime(2026,10,12), "", 1, DBNull.Value);
        data.Rows.Add("", "  ", "  ", "text", DBNull.Value);
        data.Rows.Add(" \t", "", "", "", DBNull.Value);
        data.Rows.Add(2, new DateTime(2026,10,13), "end", 3, DBNull.Value);
        foreach (bool validate in new[] { false, true })
        {
            using var source = data.CreateDataReader();
            using var reader = new ImportLengthDataReader(source, validate ? new Dictionary<string,int> { ["Label"] = 255 } : new Dictionary<string,int>(), "test.xlsx", new[] { "Additional Expense", "Created" });
            Check(reader.Read() && Convert.ToDouble(reader.GetValue(0)) == 12.5 && reader.GetValue(1) is DateTime, "Nonblank typed values changed");
            Check(reader.Read() && reader.IsDBNull(0) && reader.IsDBNull(1) && reader[0] == DBNull.Value && reader["Created"] == DBNull.Value, "Blank numeric/date values were not NULL");
            var values = new object[5]; Check(reader.GetValues(values) == 5 && values[0] == DBNull.Value && values[1] == DBNull.Value, "GetValues bypassed normalization");
            Check((string)values[2] == "  " && (string)values[3] == "text" && reader.IsDBNull(4), "Text or null content changed");
            Check(reader.Read() && reader.IsDBNull(0) && reader.IsDBNull(1) && (string)reader.GetValue(2) == "", "Whitespace handling failed");
            Check(reader.Read() && Convert.ToInt32(reader[0]) == 2 && reader[1] is DateTime && !reader.IsDBNull(0), "Null leaked into next row");
        }
        foreach (var type in new[] { "int", "decimal", "float", "date", "datetime2", "bit", "time" }) Check(SqlServerDbAccess.BlankImportValuesBecomeNull(type), "Missing target type " + type);
        foreach (var type in new[] { "nvarchar", "varchar", "text", "xml", "varbinary", "sql_variant" }) Check(!SqlServerDbAccess.BlankImportValuesBecomeNull(type), "Unexpected blank conversion for " + type);
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
    await Test("Converted policy UPDATE uses compact substr and executes correctly in SQLite", () =>
    {
        const string source = "UPDATE [dbo].[客户费用余额] SET [Policy reason] =a.[政策原因], [政策描述]=SUBSTRING(a.[政策原因], 1,60) from 客户费用政策 a where [客户费用余额].政策编码=a.[政策编码]";
        var sql = SQLiteViewMigration.ConvertQuery(SQLiteViewMigration.Tokenize(source), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo.客户费用余额", "dbo.客户费用政策" }, true);
        Check(sql.Contains("substr(") && !sql.Contains("CASE WHEN") && !sql.Contains("9223372036854775808"), "Constant substring was not simplified");
        using var db = new SQLiteConnection("Data Source=:memory:"); db.Open();
        using var create = new SQLiteCommand("CREATE TABLE 客户费用余额(政策编码 INTEGER, [Policy reason] TEXT, 政策描述 TEXT); CREATE TABLE 客户费用政策(政策编码 INTEGER, 政策原因 TEXT); INSERT INTO 客户费用余额 VALUES(1,'old','old'),(2,'old','old'),(3,'old','old'),(4,'keep','keep'); INSERT INTO 客户费用政策 VALUES(1,'short'),(2,@long),(3,NULL)",db);
        var longText = new string('政',70); create.Parameters.AddWithValue("@long",longText); create.ExecuteNonQuery();
        using var update = new SQLiteCommand(sql,db); update.ExecuteNonQuery();
        using var reader = new SQLiteCommand("SELECT [Policy reason],政策描述 FROM 客户费用余额 ORDER BY 政策编码",db).ExecuteReader();
        Check(reader.Read() && reader.GetString(1)=="short", "Short substring result differs");
        Check(reader.Read() && reader.GetString(0)==longText && reader.GetString(1)==longText.Substring(0,60), "Policy truncation differs");
        Check(reader.Read() && reader.IsDBNull(0) && reader.IsDBNull(1), "NULL propagation differs");
        Check(reader.Read() && reader.GetString(1)=="keep", "Unmatched row was updated");
        return Task.CompletedTask;
    });
    await Test("Payment policy UPDATE retains its source, target and WHERE clause", () =>
    {
        const string source = "UPDATE [dbo].[客户费用兑付明细] SET [Policy reason] =SUBSTRING(a.[政策原因], 1,100) from 客户费用政策 a where [客户费用兑付明细].政策编码=a.政策编码";
        var sql=SQLiteViewMigration.ConvertQuery(SQLiteViewMigration.Tokenize(source),new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo.客户费用兑付明细", "dbo.客户费用政策" },true);
        Check(sql.Contains("[Policy reason]") && sql.Contains("substr(") && sql.Contains("100") && sql.Contains("客户费用政策") && sql.Contains("where",StringComparison.OrdinalIgnoreCase),"UPDATE lost its source or condition");
        Check(!sql.Contains("兑付归属年份") && !sql.Contains("政策期间"),"Another statement was substituted");
        using var db=new SQLiteConnection("Data Source=:memory:"); db.Open();
        using var create=new SQLiteCommand("CREATE TABLE 客户费用兑付明细(政策编码 INTEGER,[Policy reason] TEXT); CREATE TABLE 客户费用政策(政策编码 INTEGER,政策原因 TEXT); INSERT INTO 客户费用兑付明细 VALUES(1,'old'),(2,'keep'),(3,'old'); INSERT INTO 客户费用政策 VALUES(1,@reason),(3,NULL)",db);
        var reason=new string('政',120); create.Parameters.AddWithValue("@reason",reason); create.ExecuteNonQuery();
        using var update=new SQLiteCommand(sql,db); update.ExecuteNonQuery();
        using var result=new SQLiteCommand("SELECT [Policy reason] FROM 客户费用兑付明细 ORDER BY 政策编码",db).ExecuteReader();
        Check(result.Read() && result.GetString(0)==reason.Substring(0,100),"100-character update failed");
        Check(result.Read() && result.GetString(0)=="keep","WHERE clause was lost");
        Check(result.Read() && result.IsDBNull(0),"NULL propagation failed");
        return Task.CompletedTask;
    });
    await Test("SQLite LEFT conversion simplifies constant lengths and preserves boundary behavior", () =>
    {
        string ConvertSql(string source,bool writes=false) => SQLiteViewMigration.ConvertQuery(SQLiteViewMigration.Tokenize(source),new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo.客户费用兑付明细" },writes);
        var sql=ConvertSql("UPDATE [dbo].[客户费用兑付明细] SET 兑付归属年份 =LEFT(政策期间, 4)",true);
        Check(sql.Contains("substr(") && !sql.Contains("CASE WHEN"),"Constant LEFT was not simplified");
        using var db=new SQLiteConnection("Data Source=:memory:"); db.Open();
        using var create=new SQLiteCommand("CREATE TABLE 客户费用兑付明细(政策期间 TEXT,兑付归属年份 TEXT); INSERT INTO 客户费用兑付明细(政策期间) VALUES('2026-10'),('20'),(''),(NULL)",db); create.ExecuteNonQuery();
        using var update=new SQLiteCommand(sql,db); update.ExecuteNonQuery();
        using(var result=new SQLiteCommand("SELECT 兑付归属年份 FROM 客户费用兑付明细 ORDER BY rowid",db).ExecuteReader())
        {
            Check(result.Read() && result.GetString(0)=="2026","Year extraction failed");
            Check(result.Read() && result.GetString(0)=="20","Short input failed");
            Check(result.Read() && result.GetString(0)=="","Empty input failed");
            Check(result.Read() && result.IsDBNull(0),"NULL input failed");
        }
        using var zero=new SQLiteCommand(ConvertSql("SELECT LEFT('abcdef',0)"),db); Check(Convert.ToString(zero.ExecuteScalar())=="","Zero length failed");
        var dynamicSql=ConvertSql("SELECT LEFT('abcdef',@length)"); Check(dynamicSql.Contains("CASE WHEN"),"Dynamic LEFT lost guards");
        using var dynamic=new SQLiteCommand(dynamicSql,db); dynamic.Parameters.AddWithValue("@length",DBNull.Value); Check(dynamic.ExecuteScalar()==DBNull.Value,"NULL length failed");
        dynamic.Parameters["@length"].Value=-1; try { dynamic.ExecuteScalar(); throw new Exception("Expected negative-length failure"); } catch(SQLiteException) { }
        return Task.CompletedTask;
    });
    await Test("Converted YEAR MONTH arithmetic preserves YYYYMM and NULL dates", () =>
    {
        var sql=SQLiteViewMigration.ConvertQuery(SQLiteViewMigration.Tokenize("UPDATE [dbo].[客户费用计提明细] SET [入账年月] = YEAR([入账日期])*100+MONTH([入账日期])"),new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo.客户费用计提明细" },true);
        Check(sql.Contains("* 100 +") && !sql.Contains("\\*"),"Arithmetic tokens changed");
        using var db=new SQLiteConnection("Data Source=:memory:"); db.Open();
        using var create=new SQLiteCommand("CREATE TABLE 客户费用计提明细(入账日期 TEXT,入账年月 INTEGER); INSERT INTO 客户费用计提明细(入账日期) VALUES('2026-10-13'),('2026-01-02 10:30:00'),('2025-12-31T23:59:59.1234567'),(NULL)",db); create.ExecuteNonQuery();
        using var update=new SQLiteCommand(sql,db); update.ExecuteNonQuery();
        using(var result=new SQLiteCommand("SELECT 入账年月 FROM 客户费用计提明细 ORDER BY rowid",db).ExecuteReader())
        {
            foreach(var expected in new[] {202610,202601,202512}) Check(result.Read() && Convert.ToInt32(result[0])==expected,"YEAR MONTH result differs");
            Check(result.Read() && result.IsDBNull(0),"NULL date changed");
        }
        return Task.CompletedTask;
    });
    await Test("SQLite date extraction generates compact SQL and uses native NULL behavior", () =>
    {
        var sql=SQLiteViewMigration.ConvertQuery(SQLiteViewMigration.Tokenize("SELECT YEAR(@date), MONTH(@date), DAY(@date)"),new HashSet<string>());
        Check(!sql.Contains("CASE WHEN") && !sql.Contains("9223372036854775808"),"Date functions retain error scaffolding");
        using var db=new SQLiteConnection("Data Source=:memory:"); db.Open();
        using var cmd=new SQLiteCommand(sql,db); cmd.Parameters.AddWithValue("@date","2026-10-13");
        using(var result=cmd.ExecuteReader()) Check(result.Read() && Convert.ToInt32(result[0])==2026 && Convert.ToInt32(result[1])==10 && Convert.ToInt32(result[2])==13,"Date extraction differs");
        foreach(var value in new object[] { DBNull.Value, "not a date" })
        {
            cmd.Parameters["@date"].Value=value;
            using var result=cmd.ExecuteReader(); Check(result.Read() && result.IsDBNull(0) && result.IsDBNull(1) && result.IsDBNull(2),"Native NULL date behavior differs");
        }
        return Task.CompletedTask;
    });
    await Test("SQLite customer code suffix conversion is compact and handles short or blank codes", () =>
    {
        var sql=SQLiteViewMigration.ConvertQuery(SQLiteViewMigration.Tokenize("UPDATE [dbo].[基本信息] SET [客户编码2] = substring([客户编码],4,len([客户编码])-3)"),new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo.基本信息" },true);
        Check(sql.Contains("substr(rtrim(") && !sql.Contains("CASE WHEN") && !sql.Contains("9223372036854775808"),"Suffix extraction did not simplify");
        using var db=new SQLiteConnection("Data Source=:memory:"); db.Open();
        using var create=new SQLiteCommand("CREATE TABLE 基本信息(客户编码 TEXT,客户编码2 TEXT); INSERT INTO 基本信息(客户编码) VALUES('ABC123'),('ABC123   '),('ABC'),('AB'),(''),(NULL)",db); create.ExecuteNonQuery();
        using var update=new SQLiteCommand(sql,db); update.ExecuteNonQuery();
        using var result=new SQLiteCommand("SELECT 客户编码2 FROM 基本信息 ORDER BY rowid",db).ExecuteReader();
        foreach(var expected in new[] {"123","123","","",""}) Check(result.Read() && result.GetString(0)==expected,"Suffix output differs");
        Check(result.Read() && result.IsDBNull(0),"Suffix NULL was lost");
        var different=SQLiteViewMigration.ConvertQuery(SQLiteViewMigration.Tokenize("SELECT SUBSTRING(@code,4,LEN(@other)-3)"),new HashSet<string>());
        Check(different.Contains("CASE WHEN"),"Different source expression was incorrectly simplified");
        return Task.CompletedTask;
    });
    await Test("SQLite substring conversion retains dynamic and nonpositive-start semantics", () =>
    {
        using var db = new SQLiteConnection("Data Source=:memory:"); db.Open();
        string ConvertSql(string sql) => SQLiteViewMigration.ConvertQuery(SQLiteViewMigration.Tokenize(sql), new HashSet<string>());
        using var zero = new SQLiteCommand(ConvertSql("SELECT SUBSTRING('abcdef',1,0)"),db);
        Check(Convert.ToString(zero.ExecuteScalar())=="", "Zero length differs");
        using var startZero = new SQLiteCommand(ConvertSql("SELECT SUBSTRING('abcdef',0,3)"),db);
        Check(Convert.ToString(startZero.ExecuteScalar())=="ab", "Nonpositive start lost compatibility");
        var dynamicSql=ConvertSql("SELECT SUBSTRING(@text,@start,@length)");
        Check(dynamicSql.Contains("CASE WHEN"), "Dynamic arguments lost guards");
        using var dynamic = new SQLiteCommand(dynamicSql,db);
        dynamic.Parameters.AddWithValue("@text","abcdef"); dynamic.Parameters.AddWithValue("@start",2); dynamic.Parameters.AddWithValue("@length",3);
        Check(Convert.ToString(dynamic.ExecuteScalar())=="bcd", "Dynamic substring differs");
        dynamic.Parameters["@length"].Value=DBNull.Value;
        Check(dynamic.ExecuteScalar()==DBNull.Value, "Dynamic NULL did not propagate");
        dynamic.Parameters["@length"].Value=-1;
        try { dynamic.ExecuteScalar(); throw new Exception("Expected negative-length error"); } catch(SQLiteException) { }
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
    await Test("SQLite procedure headers display Chinese and retain JSON compatibility", () =>
    {
        var directory=Path.Combine(root,"readable-metadata"); Directory.CreateDirectory(directory);
        var database=Path.Combine(directory,"DACH管理数据.db"); SQLiteConnection.CreateFile(database);
        using(var db=new SQLiteConnection(Connection(database)))
        {
            db.Open();
            var schema=new DbSchema { Name="DACH管理数据",Tables=new(),Procedures=new() { new Proc {Name="当月SO数据处理",SchemaName="dbo",CreateSql="CREATE PROCEDURE [dbo].[当月SO数据处理] AS BEGIN SELECT 1; END"} } };
            var errors=SQLiteProcedureScripts.Export(db,database,schema); Check(errors.Count==0,"Chinese procedure export failed");
        }
        var scriptPath=Directory.GetFiles(Path.Combine(directory,"StoredProcedures"),"*.txt").Single();
        var header=File.ReadLines(scriptPath,Encoding.UTF8).First();
        Check(header.Contains("DACH管理数据.db") && header.Contains("dbo.当月SO数据处理") && !header.Contains("\\u7BA1"),"Chinese header is still escaped");
        var parsed=System.Text.Json.JsonSerializer.Deserialize<SQLiteProcedureScripts.Metadata>(header.Substring("-- DataPie SQLite procedure: ".Length));
        Check(parsed.DatabaseFile=="DACH管理数据.db" && parsed.Source=="dbo.当月SO数据处理","Readable JSON failed to round-trip");
        var legacy=System.Text.Json.JsonSerializer.Serialize(parsed);
        Check(System.Text.Json.JsonSerializer.Deserialize<SQLiteProcedureScripts.Metadata>(legacy).Source==parsed.Source,"Legacy escaped header no longer reads");
        var loaded=SQLiteProcedureScripts.Load(Connection(database),"当月SO数据处理"); Check(loaded.Metadata.Source==parsed.Source,"Script loader rejected readable metadata");
        return Task.CompletedTask;
    });
    await Test("Procedure export index records success partial failure and reads legacy names", () =>
    {
        var directory=Path.Combine(root,"status-index"); Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"索引测试.db"); SQLiteConnection.CreateFile(path);
        using(var db=new SQLiteConnection(Connection(path)))
        {
            db.Open();
            var schema=new DbSchema { Tables=new(),Procedures=new()
            {
                new Proc {Name="成功",SchemaName="dbo",CreateSql="CREATE PROC dbo.[成功] AS SELECT 1;"},
                new Proc {Name="部分",SchemaName="dbo",CreateSql="CREATE PROC dbo.[部分] AS BEGIN SELECT 1; SELECT * FROM MissingTable; END"},
                new Proc {Name="失败",SchemaName="dbo",CreateSql="CREATE PROC dbo.[失败] AS SELECT * FROM MissingTable;"}
            } };
            SQLiteProcedureScripts.Export(db,path,schema);
        }
        var file=Directory.GetFiles(Path.Combine(directory,"StoredProcedures"),".datapie-*.json").Single();
        var index=System.Text.Json.JsonSerializer.Deserialize<SQLiteProcedureScripts.ProcedureExportIndex>(File.ReadAllText(file));
        Check(index.Version==2 && index.DatabaseFile=="索引测试.db" && index.Procedures.Count==3,"Index identity or counts differ");
        var success=index.Procedures.Single(p=>p.Name=="成功"); Check(success.Success && success.Status=="Success" && success.Source=="dbo.成功" && success.ScriptFile=="成功.txt","Success missing");
        var partial=index.Procedures.Single(p=>p.Name=="部分"); Check(!partial.Success && partial.Status=="Partial" && partial.Warnings.Count>0,"Partial was marked successful");
        var failed=index.Procedures.Single(p=>p.Name=="失败"); Check(!failed.Success && failed.Status=="Failed" && failed.Error!=null,"Failure context missing");
        var current=SQLiteProcedureScripts.List(Connection(path)).Select(p=>p.Name).OrderBy(n=>n).ToArray();
        File.WriteAllText(file,System.Text.Json.JsonSerializer.Serialize(index.Procedures.Select(p=>p.Name).ToArray()));
        var legacy=SQLiteProcedureScripts.List(Connection(path)).Select(p=>p.Name).OrderBy(n=>n).ToArray();
        Check(current.SequenceEqual(legacy) && current.Length==2,"Legacy index compatibility failed");
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
    await SQLiteImportChecks.Run(root, Test);
    await SqlFormattingChecks.Run(Test);
    await QueryResultsChecks.Run(Test);
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
