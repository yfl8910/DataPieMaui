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
        var proceduresPage = new ProceduresPage { Workspace = workspace };
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
        await test("Connection page recreation preserves loaded configuration and status", async () =>
        {
            var connections = workspace.Context.Connections;
            var status = workspace.Context.State.Status;
            connectionsPage = new ConnectionsPage { Workspace = workspace };
            await connectionsPage.InitializeAsync();
            Check(ReferenceEquals(connections, workspace.Context.Connections), "Recreated page reloaded connection configuration");
            Check(workspace.Context.State.Status == status && !workspace.IsOperationRunning, "Recreated page changed operation status");
        });
        await test("Import and export object previews share results without changing page inputs or selection", async () =>
        {
            string sql = workspace.Context.Form.Sql;
            workspace.Navigate("Import");
            await importPage.PreviewImportTableAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(workspace.Context.Results.Current?.Source == "Table · Items" && workspace.Context.Results.Current.RowCount == 2, "Wrong import target preview");
            Check(workspace.Context.Section == "Import" && workspace.Context.Form.Sql == sql, "Preview changed navigation or SQL editor");
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
        await test("Blazor pages render, encode input and show accessible confirmations", async () =>
        {
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<IJSRuntime, NoopJs>();
            services.AddSingleton<IErrorBoundaryLogger, BoundaryLogger>();
            await using var provider = services.BuildServiceProvider();
            workspace.Context.Form.Sql = "SELECT '<script>alert(1)</script>'";
            var pages = new[]
            {
                ("Connections", "Connect to SQL Server"), ("Query", "SQL editor"), ("Import", "Source folder"),
                ("Export", "Excel (MiniExcel)"), ("Procedures", "Run selected procedures"), ("Migration", "Choose destination and convert"), ("About", "Blazor Hybrid")
            };
            // Render the conversion page without connecting to an external SQL Server.
            workspace.Context.State.DatabaseType = "SQLSERVER";
            foreach (var (section, expected) in pages)
            {
                workspace.Navigate(section);
                if (section == "Export") exportPage.ChangeTab("View");
                await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
                string html = await renderer.Dispatcher.InvokeAsync(async () =>
                {
                    var component = await renderer.RenderComponentAsync<Workspace>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Controller"] = workspace }));
                    return component.ToHtmlString();
                });
                Check(html.Contains(expected), "Missing content for " + section);
                if (section is "Import" or "Export") Check(html.Contains("Query data"), "Object query button missing in " + section);
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
    private sealed class FakeDesktop : IDesktopServices
    {
        public int ResultWindows { get; private set; }
        public Func<Task<string?>> Save { get; set; } = () => Task.FromResult<string?>(null);
        public string? Folder { get; set; }
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
