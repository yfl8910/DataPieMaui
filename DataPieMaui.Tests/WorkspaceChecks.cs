using System.Data.SQLite;
using DataPieMaui.Components;
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
            using var command = new SQLiteCommand("CREATE TABLE Items(Id INTEGER, Name TEXT); INSERT INTO Items VALUES(1, 'first'), (2, 'second');", access);
            command.ExecuteNonQuery();
        }
        var desktop = new FakeDesktop();
        var workspace = new WorkspaceController(desktop, Path.Combine(root, "workspace-connections.db"));
        await test("Blazor workspace connects, previews and keeps inputs across pages", async () =>
        {
            await workspace.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10));
            workspace.Form.SqlitePath = path;
            await workspace.ConnectSqliteAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(workspace.State.IsConnected && workspace.Section == "Query", "Connection did not open query page");
            Check(workspace.Connections.Count == 1 && workspace.Tables.Contains("Items"), "Connection/schema missing");
            workspace.Form.Sql = "SELECT * FROM Items ORDER BY Id";
            await workspace.PreviewAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(workspace.Results.Current?.Rows.Length == 2 && desktop.ResultWindows == 1, "Preview not passed to desktop host");
            workspace.Navigate("Import"); workspace.Form.ImportPath = "remember.csv"; workspace.Navigate("Query");
            Check(workspace.Form.Sql.Contains("ORDER BY Id") && workspace.Form.ImportPath == "remember.csv", "Navigation lost inputs");
            await workspace.OpenResultsAsync();
            Check(desktop.ResultWindows == 2, "Reopen results not dispatched");
        });
        await test("Confirmation prevents execution and concurrent operations; cancel is safe", async () =>
        {
            workspace.Form.Sql = "INSERT INTO Items VALUES(3, 'third')";
            var running = workspace.ExecuteSqlAsync();
            Check(workspace.Dialogs.Current?.CanCancel == true && workspace.State.Busy, "Missing confirmation or busy guard");
            Check(Count(connection) == 2, "SQL ran before confirmation");
            int calls = desktop.ResultWindows;
            await workspace.PreviewAsync();
            workspace.Navigate("Export");
            Check(desktop.ResultWindows == calls && workspace.Section == "Query", "Concurrent action bypassed guard");
            workspace.Dialogs.Complete(false);
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Count(connection) == 2 && !workspace.State.Busy && workspace.State.Status == "Run SQL canceled.", "Cancel reported completion or changed data");
            running = workspace.ExecuteSqlAsync();
            workspace.Dialogs.Complete(true);
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Count(connection) == 3 && workspace.State.Status.Contains("rows affected"), "Confirmed SQL did not run");
        });
        await test("Save picker cancellation, CSV overwrite confirmation and export", async () =>
        {
            workspace.Form.Sql = "SELECT * FROM Items";
            desktop.Save = () => Task.FromResult<string?>(null);
            await workspace.ExportQueryAsync(true, false);
            Check(workspace.State.Status == "Export data canceled.", "Picker cancellation reported success");
            var export = Path.Combine(root, "workspace-export.csv");
            desktop.Save = () => Task.FromResult<string?>(export);
            await workspace.ExportQueryAsync(true, false).WaitAsync(TimeSpan.FromSeconds(10));
            Check(File.ReadAllText(export).Contains("third"), "Query export incomplete");
            var folder = Path.Combine(root, "workspace-exports"); Directory.CreateDirectory(folder);
            var existing = Path.Combine(folder, "Items.csv"); File.WriteAllText(existing, "keep me");
            desktop.Folder = folder; workspace.SelectAll(true);
            var running = workspace.ExportBatchAsync("csv");
            Check(workspace.Dialogs.Current?.Title == "Replace CSV files", "Missing overwrite confirmation");
            workspace.Dialogs.Complete(false); await running;
            Check(File.ReadAllText(existing) == "keep me", "Cancel overwrote the file");
            running = workspace.ExportBatchAsync("csv"); workspace.Dialogs.Complete(true); await running.WaitAsync(TimeSpan.FromSeconds(10));
            Check(File.ReadAllText(existing).Contains("third"), "Batch CSV export failed");
        });
        await test("Failed connection preserves workspace and releases operation guard", async () =>
        {
            string before = workspace.State.Connection;
            workspace.Form.SqlitePath = Path.Combine(root, "missing-workspace.db");
            var running = workspace.ConnectSqliteAsync();
            Check(workspace.Dialogs.Current?.Title.Contains("failed") == true, "No error dialog");
            Check(workspace.CanClose, "Error dialog prevents closing app");
            workspace.Dialogs.Complete(true); await running;
            Check(workspace.State.Connection == before && workspace.Results.Current != null && !workspace.IsOperationRunning, "Failure discarded workspace or left it locked");
            workspace.Form.SqlitePath = path;
        });
        await test("Blazor pages render, encode input and show accessible confirmations", async () =>
        {
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<IJSRuntime, NoopJs>();
            services.AddSingleton<IErrorBoundaryLogger, BoundaryLogger>();
            await using var provider = services.BuildServiceProvider();
            workspace.Form.Sql = "SELECT '<script>alert(1)</script>'";
            var pages = new[]
            {
                ("Connections", "Connect to SQL Server"), ("Query", "SQL editor"), ("Import", "Source folder"),
                ("Export", "Excel (MiniExcel)"), ("Procedures", "Run selected procedures"), ("Migration", "Choose destination and convert"), ("About", "Blazor Hybrid")
            };
            // Render the conversion page without connecting to an external SQL Server.
            workspace.State.DatabaseType = "SQLSERVER";
            foreach (var (section, expected) in pages)
            {
                workspace.Navigate(section);
                await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
                string html = await renderer.Dispatcher.InvokeAsync(async () =>
                {
                    var component = await renderer.RenderComponentAsync<Workspace>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Controller"] = workspace }));
                    return component.ToHtmlString();
                });
                Check(html.Contains(expected), "Missing content for " + section);
                Check(!html.Contains("<script>alert(1)</script>"), "SQL rendered as markup");
                Check(!html.Contains("workspace-body\" inert"), "Idle workspace is inert");
                if (renderUi) SaveHtml(section, html);
            }
            workspace.State.DatabaseType = "SQLITE";
            workspace.Navigate("Query");
            // Initialization has already completed, so a static render can display a pending dialog.
            var dialogTask = workspace.Dialogs.ConfirmAsync("Confirm test", "<script>not markup</script>", "Run");
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
            workspace.Dialogs.Complete(false); Check(!await dialogTask, "Dialog cancellation failed");
        });
        await test("Clear table confirmation resets the result snapshot", async () =>
        {
            var running = workspace.ClearTableAsync();
            workspace.Dialogs.Complete(false); await running;
            Check(Count(connection) == 3, "Canceled clear changed data");
            running = workspace.ClearTableAsync(); workspace.Dialogs.Complete(true); await running.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Count(connection) == 0 && workspace.Results.Current == null, "Clear left data or stale results");
        });
        await test("Workspace file and folder import use the selected target", async () =>
        {
            workspace.Form.ImportPath = Path.Combine(root, "workspace-export.csv");
            await workspace.ImportFileAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(Count(connection) == 3, "Single-file import failed");
            workspace.Form.ImportFolder = Path.Combine(root, "workspace-exports");
            await workspace.ImportDirectoryAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(Count(connection) == 6 && workspace.State.Status.StartsWith("Batch import completed"), "Batch import or completion status failed");
        });
        await test("Workspace procedure execution waits for confirmation", async () =>
        {
            string converted = Path.Combine(root, "converted.db");
            var convertedConnection = new SQLiteConnectionStringBuilder { DataSource = converted }.ToString();
            using (var access = new SQLiteConnection(convertedConnection))
            {
                access.Open(); using var command = new SQLiteCommand("DELETE FROM Items", access); command.ExecuteNonQuery();
            }
            workspace.Form.SqlitePath = converted;
            await workspace.ConnectSqliteAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(workspace.Procedures.Count == 1 && workspace.Results.Current == null, "Schema/results did not refresh on database switch");
            workspace.Procedures[0].Selected = true;
            var running = workspace.RunProceduresAsync();
            Check(workspace.Dialogs.Current?.Title == "Run stored procedures" && Count(convertedConnection) == 0, "Procedure ran before confirmation");
            workspace.Dialogs.Complete(false); await running;
            Check(Count(convertedConnection) == 0, "Canceled procedure changed data");
            running = workspace.RunProceduresAsync(); workspace.Dialogs.Complete(true); await running.WaitAsync(TimeSpan.FromSeconds(10));
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
