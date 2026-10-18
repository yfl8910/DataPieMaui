using System.Data.SQLite;
using DataPieMaui.Components;
using DataPieMaui.Components.Pages;
using DataPieMaui.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

internal static class WorkspaceChecks
{
    public static async Task Run(string root, Func<string, Func<Task>, Task> test, bool renderUi)
    {
        var path = Path.Combine(root, "workspace.db");
        SQLiteConnection.CreateFile(path);
        string connection = new SQLiteConnectionStringBuilder { DataSource = path }.ToString();
        using (var access = new SQLiteConnection(connection))
        {
            access.Open();
            using var command = new SQLiteCommand("CREATE TABLE Items(Id INTEGER, Name TEXT); INSERT INTO Items VALUES(1, 'first'), (2, 'second'); CREATE VIEW ItemNames AS SELECT Name FROM Items; CREATE VIEW ManyRows AS WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n<1001) SELECT n FROM numbers;", access);
            command.ExecuteNonQuery();
        }
        var desktop = new FakeDesktop();
        var workspace = new WorkspaceController(desktop, Path.Combine(root, "workspace-connections.db"));
        var connectionsPage = new ConnectionsPage { Workspace = workspace };
        var queryPage = new QueryPage { Workspace = workspace };
        var importPage = new ImportPage { Workspace = workspace };
        var exportPage = new ExportPage { Workspace = workspace };
        var tableDesignPage = new TableDesignPage { Workspace = workspace };
        var proceduresPage = new ProceduresPage { Workspace = workspace };
        await test("Blank SQLite creation connects without saving configuration and never overwrites files", async () =>
        {
            var blankPath = Path.Combine(root, "new-blank.db");
            var picker = new FakeDesktop();
            var blankWorkspace = new WorkspaceController(picker, Path.Combine(root, "blank-connections.db"));
            var page = new ConnectionsPage { Workspace = blankWorkspace };
            await page.InitializeAsync();
            await page.CreateSqliteAsync();
            Check(!File.Exists(blankPath) && !blankWorkspace.Context.State.IsConnected, "Canceled picker created or connected a database");
            picker.NewSave = () => Task.FromResult<string?>(blankPath);
            picker.Save = () => throw new Exception("Creation must not use the file-creating save picker" );
            await page.CreateSqliteAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(File.Exists(blankPath) && new FileInfo(blankPath).Length > 0, "SQLite file was not initialized");
            Check(blankWorkspace.Context.State.IsConnected && blankWorkspace.Context.Section == "Query" && blankWorkspace.Context.Form.SqlitePath == blankPath, "New database was not connected");
            Check(blankWorkspace.Context.Tables.Count == 0 && blankWorkspace.Context.Exports.Count == 0 && blankWorkspace.Context.Connections.Count == 0, "Database not empty or connection unexpectedly saved");
            Check(new ConnectionStore(Path.Combine(root, "blank-connections.db")).Load().Count == 0, "Connection configuration was persisted");
            using (var db = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = blankPath, Pooling = false }.ToString()))
            {
                db.Open(); using var integrity = new SQLiteCommand("PRAGMA integrity_check", db); Check(Convert.ToString(integrity.ExecuteScalar()) == "ok", "Created database is invalid");
                using var create = new SQLiteCommand("CREATE TABLE KeepMe(Id INTEGER); INSERT INTO KeepMe VALUES(1)", db); create.ExecuteNonQuery();
            }
            var bytes = File.ReadAllBytes(blankPath);
            var previous = blankWorkspace.Context.State.Connection;
            var pending = page.CreateSqliteAsync();
            for (int i = 0; i < 100 && blankWorkspace.Context.Dialogs.Current == null; i++) await Task.Delay(10);
            Check(blankWorkspace.Context.Dialogs.Current?.Title == "File already exists", "Existing destination did not produce a friendly notice");
            blankWorkspace.Context.Dialogs.Complete(true); await pending;
            Check(File.ReadAllBytes(blankPath).SequenceEqual(bytes) && blankWorkspace.Context.State.Connection == previous, "Existing database or current connection was changed");
        });
        await test("Blazor workspace connects, previews and keeps inputs across pages", async () =>
        {
            await connectionsPage.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));
            workspace.Context.Form.SqlitePath = path;
            await connectionsPage.ConnectSqliteAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(workspace.Context.State.IsConnected && workspace.Context.Section == "Query", "Connection did not open query page");
            Check(workspace.Context.Connections.Count == 1 && workspace.Context.Tables.Contains("Items"), "Connection/schema missing");
            workspace.Context.Form.Sql = "SELECT * FROM Items ORDER BY Id";
            await queryPage.PreviewAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(workspace.Context.Results.Current?.RowCount == 2 && desktop.ResultWindows == 1, "Preview not passed to desktop host");
            workspace.Navigate("Import"); workspace.Context.Form.ImportPath = "remember.csv"; workspace.Navigate("Query");
            Check(workspace.Context.Form.Sql.Contains("ORDER BY Id") && workspace.Context.Form.ImportPath == "remember.csv", "Navigation lost inputs");
            queryPage = new QueryPage { Workspace = workspace };
            await queryPage.OpenResultsAsync();
            Check(desktop.ResultWindows == 2, "Reopen results not dispatched");
        });
        await test("Excel worksheet SQL supports sheet names, types and reviewed table creation", async () =>
        {
            var excel = Path.Combine(root, "structure.xlsx");
            var rows = new[] { new Dictionary<string, object> { ["Code"] = "00123", ["Quantity"] = 4, ["Rate"] = 1.5, ["Odd]Name"] = "value" } };
            MiniExcelLibs.MiniExcel.SaveAs(excel, rows, sheetName: "Orders");
            Check(DataPieCore.ExcelTableSql.Sheets(excel).SequenceEqual(new[] { "Orders" }), "Worksheet names missing");
            var sqlServer = DataPieCore.ExcelTableSql.Generate(excel, "Orders", "SQLSERVER");
            Check(sqlServer.Contains("CREATE TABLE [Orders]") && sqlServer.Contains("[Code] NVARCHAR(255)") && sqlServer.Contains("[Quantity] INT") && sqlServer.Contains("[Rate] FLOAT") && sqlServer.Contains("[Odd]]Name]"), "Incorrect types or quoting");
            workspace.Context.Form.CreateTablePath = excel;
            workspace.Context.Form.CreateTableSheet = "Orders";
            var page = new TableDesignPage { Workspace = workspace };
            await page.GenerateAsync();
            Check(workspace.Context.Form.CreateTableSql.Contains("CREATE TABLE \"Orders\""), "Sheet name not used as table name");
            workspace.Context.Form.CreateTableSql = workspace.Context.Form.CreateTableSql.Replace("\"Orders\"", "\"ReviewedOrders\"");
            var pending = page.CreateAsync();
            Check(workspace.Context.Dialogs.Current?.Title == "Create table" && !workspace.Context.Tables.Contains("ReviewedOrders"), "Creation did not await confirmation");
            workspace.Context.Dialogs.Complete(false); await pending;
            Check(!workspace.Context.Tables.Contains("ReviewedOrders"), "Cancellation created table");
            pending = page.CreateAsync(); workspace.Context.Dialogs.Complete(true); await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Check(workspace.Context.Tables.Contains("ReviewedOrders") && workspace.Context.Exports.Any(x => x.Name == "ReviewedOrders"), "Reviewed SQL was not executed or schema not refreshed");
            using var db = new SQLiteConnection(connection); db.Open();
            using var drop = new SQLiteCommand("DROP TABLE ReviewedOrders", db); drop.ExecuteNonQuery();
            await workspace.Context.Database.ConnectAsync(workspace.Context.Database.Connection!);
            workspace.Context.Tables = workspace.Context.Database.Schema!.Tables.Select(t => t.Name).ToArray();
            workspace.Context.Exports.RemoveAll(x => x.Name == "ReviewedOrders");
        });
        await test("Excel SQL types respect INT ranges and UTF-16 length boundaries across all rows", () =>
        {
            var excel = Path.Combine(root, "type-boundaries.xlsx");
            var rows = new[]
            {
                new Dictionary<string, object?> { ["IntMax"] = int.MaxValue, ["IntMin"] = int.MinValue, ["Above"] = 1, ["Below"] = 1, ["Len255"] = new string('x',255), ["Len256"] = "short", ["Emoji255"] = string.Concat(Enumerable.Repeat("😀",127)) + "x", ["Emoji256"] = string.Concat(Enumerable.Repeat("😀",128)), ["Mixed"] = 42, ["Empty"] = null },
                new Dictionary<string, object?> { ["IntMax"] = 0, ["IntMin"] = 0, ["Above"] = (long)int.MaxValue + 1, ["Below"] = (long)int.MinValue - 1, ["Len255"] = "short", ["Len256"] = new string('x',256), ["Emoji255"] = "short", ["Emoji256"] = "short", ["Mixed"] = new string('x',256), ["Empty"] = null }
            };
            MiniExcelLibs.MiniExcel.SaveAs(excel, rows, sheetName: "Types");
            var sql = DataPieCore.ExcelTableSql.Generate(excel, "Types", "SQLSERVER");
            foreach (var name in new[] { "IntMax", "IntMin" }) Check(sql.Contains("["+name+"] INT NULL"), "INT boundary failed: "+name);
            foreach (var name in new[] { "Above", "Below" }) Check(sql.Contains("["+name+"] BIGINT NULL"), "BIGINT overflow detection failed: "+name);
            foreach (var name in new[] { "Len255", "Emoji255", "Empty" }) Check(sql.Contains("["+name+"] NVARCHAR(255) NULL"), "255-unit boundary failed: "+name);
            foreach (var name in new[] { "Len256", "Emoji256", "Mixed" }) Check(sql.Contains("["+name+"] NVARCHAR(MAX) NULL"), "Long or mixed text failed: "+name);
            var sqlite = DataPieCore.ExcelTableSql.Generate(excel, "Types", "SQLITE");
            Check(sqlite.Contains("\"Above\" INTEGER NULL") && sqlite.Contains("\"Len255\" TEXT NULL"), "SQLite types changed");
            return Task.CompletedTask;
        });
        await test("Excel inference ignores blank strings and stops after 1000 data rows", () =>
        {
            var excel = Path.Combine(root, "inference-sample.xlsx");
            var rows = Enumerable.Range(1, 1001).Select(i => new Dictionary<string, object?>
            {
                ["Numeric"] = i == 1001 ? "not numeric" : i <= 3 ? new[] { "", "   ", "\t\r\n" }[i-1] : i,
                ["Range"] = i == 1001 ? (long)int.MaxValue + 1 : 1,
                ["Text"] = i == 1001 ? new string('x',256) : "short",
                ["LastSample"] = i == 1000 ? "text at boundary" : i
            }).ToArray();
            MiniExcelLibs.MiniExcel.SaveAs(excel, rows, sheetName: "Sample");
            var sql = DataPieCore.ExcelTableSql.Generate(excel, "Sample", "SQLSERVER");
            Check(sql.Contains("[Numeric] INT NULL"), "Blank strings changed numeric inference");
            Check(sql.Contains("[Range] INT NULL") && sql.Contains("[Text] NVARCHAR(255) NULL"), "Inference read row 1001");
            Check(sql.Contains("[LastSample] NVARCHAR(255) NULL"), "Inference excluded data row 1000");
            var shorter = Path.Combine(root, "inference-short.xlsx");
            MiniExcelLibs.MiniExcel.SaveAs(shorter, rows.Take(4).ToArray(), sheetName: "Sample");
            Check(DataPieCore.ExcelTableSql.Generate(shorter, "Sample", "SQLSERVER").Contains("[Numeric] INT NULL"), "Short worksheet ignored numeric values after blanks");
            return Task.CompletedTask;
        });
        await test("Excel inference recognizes native dates and unambiguous date text", () =>
        {
            var excel = Path.Combine(root, "date-types.xlsx");
            MiniExcelLibs.MiniExcel.SaveAs(excel, new[]
            {
                new Dictionary<string, object?> { ["NativeDate"] = new DateTime(2026,10,12), ["TextDate"] = "2026-10-12", ["Combined"] = new DateTime(2026,10,12), ["ChineseDate"] = "2026年10月12日", ["Timestamp"] = "2026-10-12T09:30:15.1234567", ["Midnight"] = "2026-10-12 00:00:00", ["NativeTime"] = new DateTime(2026,10,12), ["Invalid"] = "2026-02-30", ["NumberCode"] = "20261012", ["Mixed"] = "2026-10-12", ["Ambiguous"] = "10/12/2026" },
                new Dictionary<string, object?> { ["NativeDate"] = null, ["TextDate"] = "  ", ["Combined"] = "2026/10/13", ["ChineseDate"] = "2026年1月2日", ["Timestamp"] = "2026-10-13 08:15:00", ["Midnight"] = "2026-10-13", ["NativeTime"] = new DateTime(2026,10,13,9,30,0), ["Invalid"] = "bad", ["NumberCode"] = "00123", ["Mixed"] = "not a date", ["Ambiguous"] = "11/12/2026" }
            }, sheetName: "Dates");
            var sql = DataPieCore.ExcelTableSql.Generate(excel, "Dates", "SQLSERVER");
            foreach (var name in new[] { "NativeDate", "TextDate", "Combined", "ChineseDate" })
                Check(sql.Contains("["+name+"] DATE NULL"), "Date inference failed for " + name + ": " + sql);
            foreach (var name in new[] { "Timestamp", "Midnight", "NativeTime" })
                Check(sql.Contains("["+name+"] DATETIME2 NULL"), "Time information lost for " + name);
            foreach (var name in new[] { "Invalid", "NumberCode", "Mixed", "Ambiguous" })
                Check(sql.Contains("["+name+"] NVARCHAR(255) NULL"), "Non-date text was converted: " + name);
            var sqlite = DataPieCore.ExcelTableSql.Generate(excel, "Dates", "SQLITE");
            Check(sqlite.Contains("\"TextDate\" TEXT NULL"), "SQLite date should remain TEXT");
            return Task.CompletedTask;
        });
        await test("Excel Id columns generate identity primary keys and SQLite assigns IDs", async () =>
        {
            foreach (var header in new[] { "Id", "ID", "id" })
            {
                var excel = Path.Combine(root, "identity-" + Guid.NewGuid().ToString("N") + ".xlsx");
                MiniExcelLibs.MiniExcel.SaveAs(excel, new[] { new Dictionary<string, object> { [header] = "source-id", ["Name"] = "example" } }, sheetName: "IdentityRows");
                var sqlServer = DataPieCore.ExcelTableSql.Generate(excel, "IdentityRows", "SQLSERVER");
                Check(sqlServer.Contains("[" + header + "] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY"), "SQL Server Id was inferred from Excel values instead of using identity");
                var sqlite = DataPieCore.ExcelTableSql.Generate(excel, "IdentityRows", "SQLITE");
                Check(sqlite.Contains("\"" + header + "\" INTEGER PRIMARY KEY AUTOINCREMENT"), "SQLite identity missing");
                using var db = new SQLiteConnection("Data Source=:memory:"); db.Open();
                using var create = new SQLiteCommand(sqlite, db); create.ExecuteNonQuery();
                using var insert = new SQLiteCommand("INSERT INTO IdentityRows(Name) VALUES('first'),('second'); DELETE FROM IdentityRows WHERE " + header + "=2; INSERT INTO IdentityRows(Name) VALUES('third');", db); insert.ExecuteNonQuery();
                using var checkId = new SQLiteCommand("SELECT MAX(" + header + ") FROM IdentityRows", db);
                Check(Convert.ToInt64(checkId.ExecuteScalar()) == 3, "SQLite did not generate monotonically increasing IDs");
            }
        });
        await test("Connection page recreation preserves loaded configuration and status", async () =>
        {
            var connections = workspace.Context.Connections;
            var status = workspace.Context.State.Status;
            connectionsPage = new ConnectionsPage { Workspace = workspace };
            await connectionsPage.InitializeAsync();
            Check(ReferenceEquals(connections, workspace.Context.Connections), "Recreated page reloaded connection configuration");
            Check(workspace.Context.State.Status == status && !workspace.IsOperationRunning, "Recreated page changed operation status");
        });
        await test("SQL query opens the selected table editor without changing SQL or preview", async () =>
        {
            string sql = workspace.Context.Form.Sql;
            var preview = workspace.Context.Results.Current;
            var selection = workspace.Context.Form.QueryTable;
            workspace.Navigate("Query");
            await queryPage.EditQueryTableAsync();
            Check(desktop.TableEditor?.TableName == "Items" && desktop.TableEditor.Data.Rows.Count == 2, "Query EDIT opened wrong table");
            Check(workspace.Context.Form.Sql == sql && workspace.Context.Form.QueryTable == selection
                && ReferenceEquals(preview, workspace.Context.Results.Current) && workspace.Context.Section == "Query", "Query EDIT changed the editor or preview");
            desktop.TableEditor!.Data.Dispose();
        });
        await test("Import opens table editor and export previews preserve page inputs and selection", async () =>
        {
            string sql = workspace.Context.Form.Sql;
            workspace.Navigate("Import");
            var previousResult = workspace.Context.Results.Current;
            await importPage.EditImportTableAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(desktop.TableEditor?.TableName == "Items" && desktop.TableEditor.Data.Rows.Count == 2, "Wrong import target editor");
            Check(ReferenceEquals(workspace.Context.Results.Current, previousResult), "Import edit replaced query preview");
            desktop.TableEditor!.Data.Dispose();
            Check(workspace.Context.Section == "Import" && workspace.Context.Form.Sql == sql, "Edit changed navigation or SQL editor");
            workspace.Navigate("Export");
            var view = workspace.Context.Exports.Single(x => x.Name == "ItemNames"); view.Selected = true;
            await exportPage.PreviewObjectAsync(view.Name).WaitAsync(TimeSpan.FromSeconds(10));
            Check(workspace.Context.Results.Current?.Source == "View · ItemNames" && workspace.Context.Results.Current.ColumnCount == 1 && workspace.Context.Results.Current.GetColumnName(0) == "Name", "Wrong view preview");
            Check(view.Selected && workspace.Context.Form.Sql == sql && workspace.Context.Form.ImportTable == "Items" && workspace.Context.Section == "Export", "Preview changed user choices");
            view.Selected = false;
            await exportPage.PreviewObjectAsync("ManyRows").WaitAsync(TimeSpan.FromSeconds(10));
            Check(workspace.Context.Results.Current is { Truncated: true } limited && limited.RowCount == 1000, "Object preview ignored row limit");
            var pending = exportPage.PreviewObjectAsync("not in schema");
            Check(workspace.Context.Dialogs.Current?.Title == "Query data failed", "Unknown object was not rejected");
            workspace.Context.Dialogs.Complete(true); await pending;
            workspace.Navigate("Query");
        });
        await test("Export tabs retain selections and select only the active category", () =>
        {
            exportPage.ChangeTab("Table"); exportPage.SelectAll(true);
            Check(workspace.Context.Exports.Where(x => x.Kind == "Table").All(x => x.Selected), "Tables not selected");
            Check(workspace.Context.Exports.Where(x => x.Kind == "View").All(x => !x.Selected), "Hidden views selected");
            exportPage.ChangeTab("View"); exportPage.SelectAll(true); exportPage.SelectAll(false);
            Check(workspace.Context.Exports.Where(x => x.Kind == "Table").All(x => x.Selected), "View clear changed table selection");
            workspace.Navigate("Query"); workspace.Navigate("Export");
            Check(workspace.Context.Form.ExportTab == "View", "Export tab lost during navigation");
            exportPage.ChangeTab("Table"); exportPage.SelectAll(false); workspace.Navigate("Query");
            return Task.CompletedTask;
        });
        await test("Confirmation prevents execution and concurrent operations; cancel is safe", async () =>
        {
            workspace.Context.Form.Sql = "INSERT INTO Items VALUES(3, 'third')";
            var running = queryPage.ExecuteSqlAsync();
            Check(workspace.Context.Dialogs.Current?.CanCancel == true && workspace.Context.State.Busy, "Missing confirmation or busy guard");
            Check(Count(connection) == 2, "SQL ran before confirmation");
            int calls = desktop.ResultWindows;
            await queryPage.PreviewAsync();
            await importPage.ImportFileAsync();
            await exportPage.ExportBatchAsync("csv");
            Check(workspace.Context.Dialogs.Current?.Title == "Run SQL", "Page actions do not share the confirmation/operation guard");
            workspace.Navigate("Export");
            Check(desktop.ResultWindows == calls && workspace.Context.Section == "Query", "Concurrent action bypassed guard");
            workspace.Context.Dialogs.Complete(false);
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Count(connection) == 2 && !workspace.Context.State.Busy && workspace.Context.State.Status == "Run SQL canceled.", "Cancel reported completion or changed data");
            running = queryPage.ExecuteSqlAsync();
            workspace.Context.Dialogs.Complete(true);
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Count(connection) == 3 && workspace.Context.State.Status.Contains("rows affected"), "Confirmed SQL did not run");
        });
        await test("Save picker cancellation, CSV overwrite confirmation and export", async () =>
        {
            workspace.Context.Form.Sql = "SELECT * FROM Items";
            desktop.Save = () => Task.FromResult<string?>(null);
            await queryPage.ExportQueryAsync(true, false);
            Check(workspace.Context.State.Status == "Export data canceled.", "Picker cancellation reported success");
            var export = Path.Combine(root, "workspace-export.csv");
            desktop.Save = () => Task.FromResult<string?>(export);
            await queryPage.ExportQueryAsync(true, false).WaitAsync(TimeSpan.FromSeconds(10));
            Check(File.ReadAllText(export).Contains("third"), "Query export incomplete");
            Check(workspace.Context.State.Status.Contains("Export data completed in")
                && workspace.Context.State.Status.Contains(Path.GetFullPath(export)), "Query export status missing completion or file path");
            var folder = Path.Combine(root, "workspace-exports"); Directory.CreateDirectory(folder);
            var existing = Path.Combine(folder, "Items.csv"); File.WriteAllText(existing, "keep me");
            desktop.Folder = folder; exportPage.SelectAll(false);
            workspace.Context.Exports.Single(x => x.Name == "Items").Selected = true;
            var running = exportPage.ExportBatchAsync("csv");
            Check(workspace.Context.Dialogs.Current?.Title == "Replace CSV files", "Missing overwrite confirmation");
            workspace.Context.Dialogs.Complete(false); await running;
            Check(File.ReadAllText(existing) == "keep me", "Cancel overwrote the file");
            running = exportPage.ExportBatchAsync("csv"); workspace.Context.Dialogs.Complete(true); await running.WaitAsync(TimeSpan.FromSeconds(10));
            Check(File.ReadAllText(existing).Contains("third"), "Batch CSV export failed");
            Check(workspace.Context.State.Status.Contains("Batch export completed in")
                && workspace.Context.State.Status.Contains(Path.GetFullPath(existing)), "Batch export status missing completion or file path");
        });
        await test("Failed connection preserves workspace and releases operation guard", async () =>
        {
            string before = workspace.Context.State.Connection;
            workspace.Context.Form.SqlitePath = Path.Combine(root, "missing-workspace.db");
            var running = connectionsPage.ConnectSqliteAsync();
            Check(workspace.Context.Dialogs.Current?.Title.Contains("failed") == true, "No error dialog");
            Check(workspace.CanClose, "Error dialog prevents closing app");
            workspace.Context.Dialogs.Complete(true); await running;
            Check(workspace.Context.State.Connection == before && workspace.Context.Results.Current != null && !workspace.IsOperationRunning, "Failure discarded workspace or left it locked");
            workspace.Context.Form.SqlitePath = path;
        });
        await test("View definitions load only on click, preserve selection and release on close", async () =>
        {
            var item = workspace.Context.Exports.Single(o => o.Name == "ItemNames");
            item.Selected = true;
            Check(workspace.Context.Dialogs.Current is null, "Initialization opened a definition");
            var pending = workspace.Context.Definitions.ShowAsync(item);
            for (int i = 0; i < 200 && workspace.Context.Dialogs.Current is null && !pending.IsCompleted; i++) await Task.Delay(10);
            Check(workspace.Context.Dialogs.Current?.Sql?.Contains("SELECT Name") == true, "View definition missing");
            Check(workspace.Context.Dialogs.Current?.Editor?.Original.CanEdit == true, "SQLite view editor was not connected");
            Check(item.Selected, "Viewing a definition changed selection");
            workspace.Context.Dialogs.Complete(true); await pending;
            Check(workspace.Context.Dialogs.Current is null && !workspace.IsOperationRunning, "Definition or operation retained after close");
            item.Selected = false;
            using (var db = new SQLiteConnection(connection))
            {
                db.Open(); using var command = new SQLiteCommand("DROP VIEW ItemNames; CREATE VIEW ItemNames AS SELECT Name, Id FROM Items", db); command.ExecuteNonQuery();
            }
            pending = workspace.Context.Definitions.ShowAsync(item);
            for (int i = 0; i < 200 && workspace.Context.Dialogs.Current is null && !pending.IsCompleted; i++) await Task.Delay(10);
            Check(workspace.Context.Dialogs.Current?.Sql?.Contains("Name, Id") == true, "Reopen returned a cached definition");
            workspace.Context.Dialogs.Complete(true); await pending;
        });
        await test("Procedure tabs separate parameterized execution from batch selection", async () =>
        {
            var previous = workspace.Context.Procedures.ToArray();
            var plain = new DataPieMaui.ViewModels.SelectableObject("Plain", "Procedure") { DefinitionObject = new("Procedure", "dbo", "Plain") };
            var parameterized = new DataPieMaui.ViewModels.SelectableObject("Parameterized", "Procedure") { DefinitionObject = new("Procedure", "dbo", "Parameterized") { HasParameters = true } };
            workspace.Context.Procedures.Clear(); workspace.Context.Procedures.AddRange([plain, parameterized]);
            workspace.Context.State.DatabaseType = "SQLSERVER";
            workspace.Navigate("Procedures");
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<IJSRuntime, NoopJs>(); services.AddSingleton<IErrorBoundaryLogger, BoundaryLogger>();
            await using var provider = services.BuildServiceProvider();
            try
            {
                foreach (bool withParameters in new[] { false, true })
                {
                    proceduresPage.ChangeTab(withParameters ? "WithParameters" : "WithoutParameters");
                    Check(proceduresPage.VisibleProcedures.SequenceEqual(new[] { withParameters ? parameterized : plain }), "Incorrect procedure classification");
                    await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
                    string html = await renderer.Dispatcher.InvokeAsync(async () =>
                    {
                        var component = await renderer.RenderComponentAsync<Workspace>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Controller"] = workspace }));
                        return component.ToHtmlString();
                    });
                    Check(html.Contains("Without parameters") && html.Contains("With parameters"), "Procedure tabs missing");
                    Check(html.Contains("Run with parameters") == withParameters, "Parameter action on wrong tab");
                    Check(html.Contains(">Run selected procedures</button>") != withParameters, "Batch action on wrong tab");
                    Check(html.Contains("type=\"checkbox\"") != withParameters, "Selection checkbox on wrong tab");
                    plain.Selected = true;
                }
                proceduresPage.ClearSelection(); Check(plain.Selected, "Other tab selection was cleared");
                var recreated = new ProceduresPage { Workspace = workspace };
                Check(recreated.VisibleProcedures.Single() == parameterized, "Tab was lost on page recreation");
                await proceduresPage.RunProceduresAsync();
                Check(workspace.Context.Dialogs.Current == null, "Parameter tab offered batch execution");
                proceduresPage.ChangeTab("WithoutParameters"); proceduresPage.ClearSelection(); Check(!plain.Selected, "Visible selection was not cleared");
            }
            finally
            {
                workspace.Context.Procedures.Clear(); workspace.Context.Procedures.AddRange(previous);
                workspace.Context.State.DatabaseType = "SQLITE"; workspace.Context.Form.ProcedureTab = "WithoutParameters";
            }
        });
        await test("Saved connections default to encrypted certificate trust without changing identity", () =>
        {
            var previous = workspace.Context.Connections;
            int previousId = workspace.Context.Form.SelectedConnectionId;
            var first = new Dbinfo { Id = 9001, Dbname = "first", Dbtype = "SQLSERVER", ConnectionStrings = "Server=one;Database=db;Integrated Security=true;Encrypt=Strict" };
            var second = new Dbinfo { Id = 9002, Dbname = "second", Dbtype = "SQLSERVER", ConnectionStrings = "Server=two;Database=db;Integrated Security=true" };
            try
            {
                workspace.Context.Connections = [first, second];
                connectionsPage.SelectConnection(first.Id);
                var draft = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(workspace.Context.Form.ConnectionString);
                Check(draft.TrustServerCertificate && draft.Encrypt == Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory, "Strict encryption prevented explicit certificate trust");
                Check(first.ConnectionStrings.Contains("Encrypt=Strict"), "Unsaved setting changed stored record");
                connectionsPage.SelectConnection(second.Id);
                var next = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(workspace.Context.Form.ConnectionString);
                Check(next.TrustServerCertificate && next.DataSource == "two", "Saved connection defaults or identity incorrect");
            }
            finally { workspace.Context.Connections = previous; connectionsPage.SelectConnection(previousId); }
            return Task.CompletedTask;
        });
        await test("Schema browser shows cached objects, reads table details and loads definitions only on request", async () =>
        {
            var services = new ServiceCollection().AddLogging();
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                SchemaHarness? browser = null;
                var component = await renderer.RenderComponentAsync<SchemaHarness>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["HostWorkspace"] = workspace, ["Capture"] = (Action<SchemaHarness>)(value => browser = value)
                }));
                Check(component.ToHtmlString().Contains("Search objects") && component.ToHtmlString().Contains("ItemNames"), "Objects or search missing");
                int loadedDefinitions = workspace.Context.Database.Schema!.ViewDefinitions.Count;
                await browser!.SelectAsync(new DBUtil.DefinitionObject("Table", "main", "Items")); browser.Render();
                var html = component.ToHtmlString();
                Check(html.Contains("Primary key") && html.Contains("Computed") && html.Contains("not loaded in this view"), "Column metadata or unloaded detail notice missing");
                Check(workspace.Context.Database.Schema.ViewDefinitions.Count == loadedDefinitions, "Selecting table eagerly loaded SQL definitions");
                var view = workspace.Context.Database.Schema.DefinitionObjects.First(o => o.Kind == "View");
                await browser.SelectAsync(view); browser.Render();
                Check(component.ToHtmlString().Contains("View definition"), "Definition action missing");
                var pending = browser.ShowDefinitionAsync();
                for (int i = 0; i < 200 && workspace.Context.Dialogs.Current == null; i++) await Task.Delay(10);
                Check(!string.IsNullOrEmpty(workspace.Context.Dialogs.Current?.Sql), "Definition not read on demand");
                workspace.Context.Dialogs.Complete(true); await pending;
                await browser.RefreshAsync(); browser.Render();
                Check(component.ToHtmlString().Contains("Select a table, view or procedure"), "Refresh retained stale selection");
            });
        });
        await test("Blazor pages render, encode input and show accessible confirmations", async () =>
        {
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<IJSRuntime, NoopJs>();
            services.AddSingleton<IErrorBoundaryLogger, BoundaryLogger>();
            await using var provider = services.BuildServiceProvider();
            workspace.Context.Form.Sql = "SELECT '<script>alert(1)</script>'";
            var pages = new[]
            {
                ("Connections", "Connect to SQL Server"), ("Query", "SQL editor"), ("TableDesign", "Excel worksheet"), ("TableDesign", "Table to delete"), ("DatabaseSchema", "Database schema"), ("Import", "Source folder"),
                ("Export", "Excel (MiniExcel)"), ("Procedures", "Run selected procedures"), ("Migration", "Choose destination and convert"), ("Migration", "Choose destination and generate script"), ("About", "Blazor Hybrid")
            };
            // Render the conversion page without connecting to an external SQL Server.
            workspace.Context.State.DatabaseType = "SQLSERVER";
            foreach (var (section, expected) in pages)
            {
                workspace.Navigate(section);
                if (section == "Export") exportPage.ChangeTab("View");
                if (section == "Import") importPage.ChangeTab("Batch");
                if (section == "Migration") workspace.Context.State.DatabaseType = expected == "Choose destination and generate script" ? "SQLITE" : "SQLSERVER";
                if (section == "TableDesign") tableDesignPage.ChangeTab(expected == "Table to delete" ? "Drop" : "Create");
                await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
                string html = await renderer.Dispatcher.InvokeAsync(async () =>
                {
                    var component = await renderer.RenderComponentAsync<Workspace>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Controller"] = workspace }));
                    return component.ToHtmlString();
                });
                Check(html.Contains(expected), "Missing content for " + section);
                if (section == "Migration")
                {
                    string otherAction = expected == "Choose destination and generate script" ? "Choose destination and convert" : "Choose destination and generate script";
                    Check(!html.Contains(otherAction) && !html.Contains("Database migration direction"), "Migration shows an irrelevant direction or direction tabs");
                    Check(html.Contains("<h2>SQL Server object script</h2>") == (expected == "Choose destination and convert"), "SQL Server object script action shown for wrong provider");
                }
                if (section == "Import") Check(!html.Contains("Drop table"), "Deletion remains on import page" );
                if (section == "TableDesign") Check(html.Contains("Generate SQL") == (expected == "Excel worksheet"), "Table design tabs show the wrong form" );
                if (section == "Import") Check(html.Contains(">EDIT</button>") && !html.Contains("Query data"), "Import edit button missing or old preview button retained");
                if (section == "Query") Check(html.Contains(">EDIT</button>"), "SQL query edit button missing");
                if (section == "Export") Check(html.Contains("Query data"), "Object query button missing in " + section);
                if (section == "Procedures") Check(!html.Contains("object-preview"), "Procedures incorrectly offer table preview");
                if (section == "Export") Check(html.Contains("View definition") && !html.Contains("SQL definition"), "Definition action missing or SQL eagerly shown");
                Check(!html.Contains("<script>alert(1)</script>"), "SQL rendered as markup");
                Check(!html.Contains("workspace-body\" inert"), "Idle workspace is inert");
                if (renderUi) SaveHtml(section, html);
            }
            await using (var definitionRenderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>()))
            {
                string definitionHtml = await definitionRenderer.Dispatcher.InvokeAsync(async () =>
                {
                    var component = await definitionRenderer.RenderComponentAsync<DataPieMaui.Components.Shared.SqlDefinitionView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Sql"] = "SELECT '<script>alert(1)</script>'" }));
                    return component.ToHtmlString();
                });
                Check(!definitionHtml.Contains("<script>") && definitionHtml.Contains("&lt;script&gt;") && definitionHtml.Contains("Copy SQL"), "Definition is not encoded or copy action missing");
            }
            var editSession = new DataPieMaui.Services.Shared.DefinitionEditorSession(
                new DBUtil.DefinitionObject("View", "dbo", "Example", 1),
                new DBUtil.ObjectDefinition("CREATE VIEW dbo.Example AS SELECT 1 AS Id") { CanEdit = true },
                "Test server / database", (_, _) => throw new Exception("Rendering must not save"));
            editSession.BeginEdit(); editSession.Draft = "CREATE VIEW dbo.Example AS SELECT '<script>bad</script>' AS Id";
            var editDialog = workspace.Context.Dialogs.ShowDefinitionAsync("dbo.Example", editSession.Original.Sql, "", editSession);
            Check(!workspace.CanClose, "Native window close ignored dirty draft");
            workspace.NotifyCloseBlocked(); Check(editSession.DiscardAction == "close", "Window close did not prompt");
            editSession.KeepEditing(); editSession.Review();
            await using (var editorRenderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>()))
            {
                string editorHtml = await editorRenderer.Dispatcher.InvokeAsync(async () =>
                {
                    var component = await editorRenderer.RenderComponentAsync<DataPieMaui.Components.Shared.WorkspaceDialogView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Dialogs"] = workspace.Context.Dialogs }));
                    return component.ToHtmlString();
                });
                Check(editorHtml.Contains("Confirm save") && editorHtml.Contains("Test server / database") && editorHtml.Contains("definition-diff"), "Save review missing target or diff");
                Check(!editorHtml.Contains("<script>bad</script>"), "Draft SQL rendered as markup");
                if (renderUi) SaveHtml("DefinitionEdit", editorHtml);
            }
            workspace.Context.Dialogs.Complete(false); await editDialog;
            var procedureCompletion = new TaskCompletionSource<DBUtil.ProcedureExecutionResult>();
            var procedureSession = new DataPieMaui.Services.Shared.ProcedureExecutionSession("server / db", new("Procedure", "dbo", "Run"),
                [new("@Text", System.Data.SqlDbType.NVarChar, 100, 0, 0, System.Data.ParameterDirection.Input)], _ => procedureCompletion.Task);
            procedureSession.Inputs[0].Text = "<script>bad</script>";
            procedureSession.Review();
            var procedureDialog = workspace.Context.Dialogs.ShowProcedureAsync(procedureSession);
            await using (var procedureRenderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>()))
            {
                string html = await procedureRenderer.Dispatcher.InvokeAsync(async () =>
                {
                    var component = await procedureRenderer.RenderComponentAsync<DataPieMaui.Components.Shared.WorkspaceDialogView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Dialogs"] = workspace.Context.Dialogs }));
                    return component.ToHtmlString();
                });
                Check(html.Contains("Confirm run") && html.Contains("server / db") && !html.Contains("<script>bad</script>"), "Procedure confirmation target or encoding failed");
            }
            var runningProcedure = procedureSession.ConfirmAsync();
            Check(!workspace.CanClose, "Native window allowed closing during procedure execution");
            procedureCompletion.SetResult(new(-1, 0, new Dictionary<string, object>())); await runningProcedure;
            Check(workspace.CanClose, "Native close remained blocked after execution");
            workspace.Context.Dialogs.Complete(true); await procedureDialog;
            workspace.Context.State.DatabaseType = "SQLITE";
            workspace.Navigate("Query");
            // Initialization has already completed, so a static render can display a pending dialog.
            var dialogTask = workspace.Context.Dialogs.ConfirmAsync("Confirm test", "<script>not markup</script>", "Run");
            await using (var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>()))
            {
                string html = await renderer.Dispatcher.InvokeAsync(async () =>
                {
                    var component = await renderer.RenderComponentAsync<Workspace>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Controller"] = workspace }));
                    return component.ToHtmlString();
                });
                Check(html.Contains("<dialog") && html.Contains("aria-labelledby=\"dialog-title\"") && html.Contains(" inert"), "Modal accessibility contract missing");
                Check(!html.Contains("<script>not markup</script>"), "Dialog message rendered as markup");
                if (renderUi) SaveHtml("Dialog", html);
            }
            workspace.Context.Dialogs.Complete(false); Check(!await dialogTask, "Dialog cancellation failed");
        });
        await test("Import tabs retain inputs and Drop table requires confirmation and refreshes schema", async () =>
        {
            var file = workspace.Context.Form.ImportPath;
            var folder = workspace.Context.Form.ImportFolder;
            importPage.ChangeTab("File"); importPage.ChangeTab("Batch"); tableDesignPage.ChangeTab("Drop");
            Check(workspace.Context.Form.ImportPath == file && workspace.Context.Form.ImportFolder == folder, "Tabs discarded import inputs");
            const string target = "Drop ` odd.name";
            using (var db = new SQLiteConnection(connection))
            {
                db.Open(); using var create = new SQLiteCommand("CREATE TABLE `Drop `` odd.name`(Id INTEGER); INSERT INTO `Drop `` odd.name` VALUES(1)", db); create.ExecuteNonQuery();
            }
            await workspace.Context.RefreshSchemaAsync();
            var originalImport = workspace.Context.Form.ImportTable;
            var originalQuery = workspace.Context.Form.QueryTable;
            workspace.Context.Form.ImportTable = workspace.Context.Form.QueryTable = target;
            workspace.Context.Form.DropTable = target;
            var pending = tableDesignPage.DropTableAsync();
            Check(workspace.Context.Dialogs.Current?.Title == "Drop table" && workspace.Context.Tables.Contains(target), "Table deleted before confirmation");
            workspace.Context.Dialogs.Complete(false); await pending;
            Check(workspace.Context.Tables.Contains(target), "Cancel deleted table");
            pending = tableDesignPage.DropTableAsync(); workspace.Context.Dialogs.Complete(true); await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!workspace.Context.Tables.Contains(target) && !workspace.Context.Exports.Any(x => x.Name == target), "Deleted table remained in lists");
            Check(workspace.Context.Form.ImportTable == "" && workspace.Context.Form.QueryTable == "" && workspace.Context.Form.DropTable == "" && workspace.Context.Results.Current == null, "Stale selection or result remained");
            using (var db = new SQLiteConnection(connection))
            {
                db.Open(); using var query = new SQLiteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Drop ` odd.name'", db);
                Check(Convert.ToInt64(query.ExecuteScalar()) == 0, "DROP TABLE did not remove structure");
            }
            Check(DataPieCore.SqlQueryBuilder.BuildDropTable("[dbo].[Odd]]Name]", "SQLSERVER") == "DROP TABLE [dbo].[Odd]]Name]", "SQL Server table name quoting failed");
            workspace.Context.Form.ImportTable = originalImport; workspace.Context.Form.QueryTable = originalQuery;
            importPage.ChangeTab("File");
        });
        await test("Schema table details open the editor without a standalone Table Edit menu", async () =>
        {
            workspace.Navigate("DatabaseSchema");
            var page = new DatabaseSchemaView { Workspace = workspace };
            await page.SelectAsync(new DBUtil.DefinitionObject("Table", "main", "Items"));
            await page.EditSelectedTableAsync();
            Check(desktop.TableEditor?.TableName == "Items" && desktop.TableEditor.Data.Rows.Count > 0, "Schema editor did not receive selected table");
            Check(workspace.Context.Tables.Contains("Items"), "Edit removed table");
            desktop.TableEditor!.Data.Dispose();
        });
        await test("SQLite Drop table rejects foreign-key references without deleting data", async () =>
        {
            using (var db = new SQLiteConnection(connection))
            {
                db.Open(); using var create = new SQLiteCommand("CREATE TABLE DropParent(Id INTEGER PRIMARY KEY); CREATE TABLE DropChild(ParentId INTEGER REFERENCES DropParent(Id) ON DELETE CASCADE); INSERT INTO DropParent VALUES(1); INSERT INTO DropChild VALUES(1)", db); create.ExecuteNonQuery();
            }
            await workspace.Context.RefreshSchemaAsync();
            try { await workspace.Context.Database.DropTableAsync("DropParent"); throw new Exception("Expected foreign key rejection"); }
            catch (InvalidOperationException) { }
            using (var db = new SQLiteConnection(connection))
            {
                db.Open(); using var count = new SQLiteCommand("SELECT COUNT(*) FROM DropChild", db); Check(Convert.ToInt64(count.ExecuteScalar()) == 1, "Referenced data was deleted");
                using var clean = new SQLiteCommand("DROP TABLE DropChild; DROP TABLE DropParent", db); clean.ExecuteNonQuery();
            }
            await workspace.Context.RefreshSchemaAsync();
        });
        await test("Clear table confirmation resets the result snapshot", async () =>
        {
            var running = importPage.ClearTableAsync();
            workspace.Context.Dialogs.Complete(false); await running;
            Check(Count(connection) == 3, "Canceled clear changed data");
            running = importPage.ClearTableAsync(); workspace.Context.Dialogs.Complete(true); await running.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Count(connection) == 0 && workspace.Context.Results.Current == null, "Clear left data or stale results");
        });
        await test("Workspace file and folder import use the selected target", async () =>
        {
            workspace.Context.Form.ImportPath = Path.Combine(root, "workspace-export.csv");
            await importPage.ImportFileAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(Count(connection) == 3, "Single-file import failed");
            workspace.Context.Form.ImportFolder = Path.Combine(root, "workspace-exports");
            await importPage.ImportDirectoryAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(Count(connection) == 6 && workspace.Context.State.Status.StartsWith("Batch import completed"), "Batch import or completion status failed");
        });
        await test("Workspace procedure execution waits for confirmation", async () =>
        {
            string converted = Path.Combine(root, "converted.db");
            var convertedConnection = new SQLiteConnectionStringBuilder { DataSource = converted }.ToString();
            using (var access = new SQLiteConnection(convertedConnection))
            {
                access.Open(); using var command = new SQLiteCommand("DELETE FROM Items", access); command.ExecuteNonQuery();
            }
            workspace.Context.Form.SqlitePath = converted;
            await connectionsPage.ConnectSqliteAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(workspace.Context.Procedures.Count == 1 && workspace.Context.Results.Current == null, "Schema/results did not refresh on database switch");
            workspace.Context.Procedures[0].Selected = true;
            var procedureItem = workspace.Context.Procedures.Single();
            var definitionTask = workspace.Context.Definitions.ShowAsync(procedureItem);
            for (int i = 0; i < 200 && workspace.Context.Dialogs.Current is null && !definitionTask.IsCompleted; i++) await Task.Delay(10);
            Check(workspace.Context.Dialogs.Current?.Sql?.Contains("INSERT") == true, "Procedure definition missing");
            Check(Count(convertedConnection) == 0, "Viewing definition executed the procedure");
            workspace.Context.Dialogs.Complete(true); await definitionTask;
            var running = proceduresPage.RunProceduresAsync();
            Check(workspace.Context.Dialogs.Current?.Title == "Run stored procedures" && Count(convertedConnection) == 0, "Procedure ran before confirmation");
            workspace.Context.Dialogs.Complete(false); await running;
            Check(Count(convertedConnection) == 0, "Canceled procedure changed data");
            running = proceduresPage.RunProceduresAsync(); workspace.Context.Dialogs.Complete(true); await running.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Count(convertedConnection) == 1, "Confirmed procedure did not execute");
        });
    }

    private static void SaveHtml(string name, string html)
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "tmp", "ui-review"); Directory.CreateDirectory(directory);
        string css = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "DataPieMaui", "wwwroot", "workspace.css"));
        File.WriteAllText(Path.Combine(directory, name + ".html"), "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><style>" + css + "</style><body>" + html + "</body></html>");
    }
    private static long Count(string connection)
    {
        using var access = new SQLiteConnection(connection); access.Open();
        using var command = new SQLiteCommand("SELECT COUNT(*) FROM Items", access); return (long)command.ExecuteScalar();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public sealed class SchemaHarness : DatabaseSchemaView
    {
        [Parameter] public WorkspaceController HostWorkspace { get; set; } = default!;
        [Parameter] public Action<SchemaHarness>? Capture { get; set; }
        protected override void OnInitialized() { Workspace = HostWorkspace; Capture?.Invoke(this); }
        public void Render() => StateHasChanged();
    }
    private sealed class FakeDesktop : IDesktopServices
    {
        public int ResultWindows { get; private set; }
        public DataPieCore.TableEditorSession? TableEditor { get; private set; }
        public Task ShowTableEditorAsync(DataPieCore.TableEditorSession session, string connectionLabel) { TableEditor = session; return Task.CompletedTask; }
        public Func<Task<string?>> Save { get; set; } = () => Task.FromResult<string?>(null);
        public Func<Task<string?>> NewSave { get; set; } = () => Task.FromResult<string?>(null);
        public Task<string?> SelectNewFilePathAsync(string name, string extension) => NewSave();
        public string? Folder { get; set; }
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public Task<string?> OpenAsync(params string[] extensions) => Task.FromResult<string?>(null);
        public Task<string?> SaveAsync(string name, string extension) => Save();
        public Task<string?> FolderAsync() => Task.FromResult(Folder);
        public Task ShowResultsAsync(QueryResultSession session) { ResultWindows++; return Task.CompletedTask; }
    }
    private sealed class NoopJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
    private sealed class BoundaryLogger : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) => ValueTask.FromException(exception);
    }
}
