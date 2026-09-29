using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using DataPieCore;
using DBUtil;
using DataPieMaui.Services;
using DataPieMaui.ViewModels;

namespace DataPieMaui;

public partial class MainPage : ContentPage
{
    private readonly WorkspaceState state = new();
    private readonly DatabaseOperations database = new();
    private readonly ConnectionStore store;
    private readonly List<SelectableObject> exports = new();
    private readonly List<SelectableObject> procedures = new();
    private DataTable? previewTable;
    private bool initialized;
    private int selectedConnectionId;

    public MainPage()
    {
        InitializeComponent();
        BindingContext = state;
        SqlServerTab.IsChecked = true;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DataPie");
        Directory.CreateDirectory(directory);
        store = new ConnectionStore(Path.Combine(directory, "connections.db"));
        ConnectionType.ItemsSource = new[] { "SQLSERVER", "SQLITE" };
        ConnectionType.SelectedIndex = 0;
        Authentication.ItemsSource = new[] { "Windows", "SQL Server" };
        Authentication.SelectedIndex = 0;
        ExportObjects.ItemsSource = exports;
        ProcedureObjects.ItemsSource = procedures;
    }

    private void ConnectionTabChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (!e.Value || sender is not RadioButton tab) return;
        string? selected = tab.Value as string;
        SqlServerConnectionPanel.IsVisible = selected == "SqlServer";
        SqliteConnectionPanel.IsVisible = selected == "Sqlite";
        SavedConnectionPanel.IsVisible = selected == "Saved";
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (initialized) return;
        initialized = true;
        await RunAsync("Load connections", async () =>
        {
            await Task.Run(() =>
            {
                foreach (var path in new[] { Path.Combine(AppContext.BaseDirectory, "data.db"), Path.Combine(Environment.CurrentDirectory, "data.db") }.Distinct(StringComparer.OrdinalIgnoreCase))
                    if (File.Exists(path)) store.ImportLegacy(path);
            });
            ReloadConnections();
        });
    }

    public bool IsOperationRunning => state.Busy;
    public void NotifyCloseBlocked()
    {
        if (state.Converting) CancelConversion(this, EventArgs.Empty);
        else state.Status = "An operation is running. Please wait until it finishes before closing the window.";
    }
    private async Task RunAsync(string title, Func<Task> action)
    {
        if (state.Busy) return;
        state.Busy = true;
        state.Status = title + "…";
        var watch = Stopwatch.StartNew();
        try
        {
            await action();
            if (state.Status == title + "…") state.Status = $"{title} completed in {watch.Elapsed.TotalSeconds:F1} s";
        }
        catch (OperationCanceledException)
        {
            state.Status = "Conversion canceled. The output database may be incomplete. Export it again before use.";
        }
        catch (ApplicationException ex) when (SqlServerToSQLite._cancelled && ex.Message == "User cancelled the conversion")
        {
            state.Status = "Conversion canceled. The output database may be incomplete. Export it again before use.";
        }
        catch (Exception ex)
        {
            state.Status = title + " failed: " + ex.Message;
            await DisplayAlertAsync(title + " failed", ex.Message, "OK");
        }
        finally { state.Busy = false; }
    }

    private void Navigate(object? sender, EventArgs e) => ShowPanel((string)((Button)sender!).CommandParameter);
    private void ShowPanel(string name)
    {
        if (!state.IsConnected || (name == "Migration" && !state.CanConvertToSqlite))
            name = "Connections";
        ConnectionsPanel.IsVisible = name == "Connections";
        QueryPanel.IsVisible = name == "Query";
        ImportPanel.IsVisible = name == "Import";
        ExportPanel.IsVisible = name == "Export";
        ProceduresPanel.IsVisible = name == "Procedures";
        MigrationPanel.IsVisible = name == "Migration";
    }
    private void ReloadConnections() => SavedConnections.ItemsSource = store.Load();
    private void SavedSelected(object? sender, EventArgs e)
    {
        if (SavedConnections.SelectedItem is not Dbinfo item) return;
        selectedConnectionId = item.Id;
        ConnectionName.Text = item.Dbname;
        ConnectionType.SelectedItem = item.Dbtype;
        ConnectionString.Text = item.ConnectionStrings;
    }
    private Dbinfo ReadConnection() => new() { Id = selectedConnectionId, Dbname = ConnectionName.Text?.Trim() ?? "", Dbtype = (string?)ConnectionType.SelectedItem ?? "", ConnectionStrings = ConnectionString.Text };
    private static DatabaseConnectionInfo Info(Dbinfo record) => new(record.Dbtype, record.ConnectionStrings, record.Dbname);
    private async Task ConnectAsync(Dbinfo record, bool save)
    {
        await database.ConnectAsync(Info(record));
        state.DatabaseType = database.Connection!.DatabaseType;
        BindSchema();
        state.Connection = record.Dbname + " · " + record.Dbtype;
        if (save) { store.Save(record, true); ReloadConnections(); }
        ShowPanel("Query");
    }
    private void BindSchema()
    {
        var names = database.Schema!.Tables.Select(t => t.Name).ToList();
        QueryTable.ItemsSource = names;
        ImportTable.ItemsSource = names.ToList();
        QueryTable.SelectedIndex = names.Count > 0 ? 0 : -1;
        ImportTable.SelectedIndex = names.Count > 0 ? 0 : -1;
        Results.SetTable(null);
        previewTable?.Dispose();
        previewTable = null;
        SqlEditor.Text = "";
        ImportPath.Text = "";
        ImportFolder.Text = "";
        exports.Clear();
        exports.AddRange(names.Select(n => new SelectableObject(n, "Table")));
        exports.AddRange(database.Schema.ViewNames.Select(n => new SelectableObject(n, "View")));
        procedures.Clear();
        procedures.AddRange(database.Schema.Procedures.Select(p => new SelectableObject(p.Name, "Procedure")));
        ExportObjects.ItemsSource = null; ExportObjects.ItemsSource = exports;
        ProcedureObjects.ItemsSource = null; ProcedureObjects.ItemsSource = procedures;
    }
    private async void ConnectSaved(object? s, EventArgs e) => await RunAsync("Connect to database", () => ConnectAsync(SavedConnections.SelectedItem as Dbinfo ?? throw new InvalidOperationException("Select a connection."), false));
    private async void ConnectEdited(object? s, EventArgs e) => await RunAsync("Connect to database", () => ConnectAsync(ReadConnection(), false));
    private async void TestConnection(object? s, EventArgs e) => await RunAsync("Test connection", async () =>
    {
        var info = Info(ReadConnection());
        await Task.Run(() => { using var access = DbAccessFactory.Create(info.ConnectionString, info.DatabaseType); access.conn.Open(); });
    });
    private async void AddConnection(object? s, EventArgs e) => await RunAsync("Save connection", () => { var record = ReadConnection(); record.Id = 0; store.Save(record); ReloadConnections(); return Task.CompletedTask; });
    private async void UpdateConnection(object? s, EventArgs e) => await RunAsync("Update connection", () =>
    {
        if (selectedConnectionId <= 0) throw new InvalidOperationException("Select the connection to update.");
        store.Save(ReadConnection()); ReloadConnections(); return Task.CompletedTask;
    });
    private async void DeleteConnection(object? s, EventArgs e) => await RunAsync("Delete connection", async () =>
    {
        if (SavedConnections.SelectedItem is not Dbinfo item) throw new InvalidOperationException("Select a connection.");
        if (!await DisplayAlertAsync("Delete connection", "Delete only the saved connection configuration: " + item.Dbname, "Delete", "Cancel")) return;
        store.Delete(item.Id); selectedConnectionId = 0; ReloadConnections();
    });
    private async void ImportConnections(object? s, EventArgs e) => await RunAsync("Import legacy connections", async () =>
    {
        var path = await WindowsFiles.OpenAsync(".db");
        if (path == null) return;
        await Task.Run(() => store.ImportLegacy(path)); ReloadConnections();
    });
    private void AuthenticationChanged(object? s, EventArgs e)
    {
        UserName.IsEnabled = Password.IsEnabled = Authentication.SelectedIndex == 1;
    }
    private DBConfig SqlConfig(string name)
    {
        if (string.IsNullOrWhiteSpace(Server.Text)) throw new InvalidOperationException("Enter a server address.");
        return new DBConfig { ProviderName = "SQLSERVER", ServerName = Server.Text.Trim(), DataBase = name, ValidataType = (string?)Authentication.SelectedItem ?? "Windows", UserName = UserName.Text, UserPwd = Password.Text };
    }
    private async void LoadDatabases(object? s, EventArgs e) => await RunAsync("Load databases", async () =>
    {
        var connection = SqlConfig("master").GetSQLmasterConstring();
        Databases.ItemsSource = await Task.Run(() => { using var access = DbAccessFactory.Create(connection, "SQLSERVER"); return access.GetDataBaseInfo(); });
        Databases.SelectedIndex = Databases.Items.Count > 0 ? 0 : -1;
    });
    private async void ConnectServer(object? s, EventArgs e) => await RunAsync("Connect to SQL Server", () =>
    {
        var name = Databases.SelectedItem as string ?? throw new InvalidOperationException("Select a database.");
        return ConnectAsync(new Dbinfo { Dbname = name, Dbtype = "SQLSERVER", ConnectionStrings = SqlConfig(name).GetConstring() }, true);
    });
    private async void BrowseSqlite(object? s, EventArgs e) => await RunAsync("Select SQLite file", async () => { var path = await WindowsFiles.OpenAsync(".db", ".sqlite", ".sqlite3"); if (path != null) SqlitePath.Text = path; });
    private async void ConnectSqlite(object? s, EventArgs e) => await RunAsync("Connect to SQLite", () =>
    {
        if (!File.Exists(SqlitePath.Text)) throw new FileNotFoundException("Select an existing SQLite database.");
        var path = Path.GetFullPath(SqlitePath.Text);
        return ConnectAsync(new Dbinfo { Dbname = Path.GetFileNameWithoutExtension(path), Dbtype = "SQLITE", ConnectionStrings = new DBConfig { ProviderName = "SQLITE", DataBase = path }.GetConstring() }, true);
    });
    private string Type => database.Connection?.DatabaseType ?? throw new InvalidOperationException("Connect to a database first.");
    private static string SelectedTable(Picker picker) => picker.SelectedItem as string ?? throw new InvalidOperationException("Select a table.");
    private void Generate(string kind)
    {
        var name = SelectedTable(QueryTable);
        var table = database.Schema!.Tables.First(t => t.Name == name);
        SqlEditor.Text = kind switch { "UPDATE" => SqlWriter.WriteUpdate(table, Type), "DELETE" => SqlWriter.WriteDelete(table, Type), _ => SqlWriter.WriteSelect(table, Type, 1000) };
    }
    private async void GenerateSelect(object? s, EventArgs e) => await RunAsync("Generate SELECT", () => { Generate("SELECT"); return Task.CompletedTask; });
    private async void GenerateUpdate(object? s, EventArgs e) => await RunAsync("Generate UPDATE", () => { Generate("UPDATE"); return Task.CompletedTask; });
    private async void GenerateDelete(object? s, EventArgs e) => await RunAsync("Generate DELETE", () => { Generate("DELETE"); return Task.CompletedTask; });
    private string QuerySql()
    {
        if (string.IsNullOrWhiteSpace(SqlEditor.Text)) throw new InvalidOperationException("Enter SQL.");
        return SqlEditor.Text;
    }
    private async void Preview(object? s, EventArgs e) => await RunAsync("Query", async () =>
    {
        if (string.IsNullOrWhiteSpace(SqlEditor.Text)) Generate("SELECT");
        var sql = QuerySql();
        var result = await Task.Run(() => { using var access = database.OpenAccess(); var table = QueryPreview.Load(access, sql, 1000, out bool truncated); return (table, truncated); });
        Results.SetTable(result.table);
        previewTable?.Dispose(); previewTable = result.table;
        state.Status = $"Query completed: {result.table.Rows.Count:N0} rows / {result.table.Columns.Count} columns" + (result.truncated ? " · Showing the first 1,000 rows. Export to retrieve the full result." : "");
    });
    private async void ExecuteSql(object? s, EventArgs e) => await RunAsync("Run SQL", async () =>
    {
        var sql = QuerySql();
        if (!await DisplayAlertAsync("Run SQL", "Run the SQL in the editor against the current database?", "Run", "Cancel")) return;
        int count = await Task.Run(() => { using var access = database.OpenAccess(); return access.ExecuteSql(sql); });
        state.Status = $"SQL completed: {count:N0} rows affected";
    });
    private async Task ExportQueryAsync(bool csv, bool wholeTable) => await RunAsync("Export data", async () =>
    {
        string name = wholeTable ? SelectedTable(QueryTable) : "Query";
        string sql = wholeTable ? SqlQueryBuilder.BuildSelectAll(name, Type) : QuerySql();
        string? path = await WindowsFiles.SaveAsync(name, csv ? ".csv" : ".xlsx");
        if (path == null) return;
        await Task.Run(() => { using var access = database.OpenAccess(); using var reader = access.GetDataReader(sql); if (csv) CsvExporter.SaveCsv(reader, path); else ExcelIO.SaveMiniExcel(path, reader, name); });
    });
    private async void ExportQueryExcel(object? s, EventArgs e) => await ExportQueryAsync(false, false);
    private async void ExportQueryCsv(object? s, EventArgs e) => await ExportQueryAsync(true, false);
    private async void ExportTableExcel(object? s, EventArgs e) => await ExportQueryAsync(false, true);
    private async void ExportTableCsv(object? s, EventArgs e) => await ExportQueryAsync(true, true);
    private async void CopyRow(object? s, EventArgs e) => await RunAsync("Copy selected row", () => Clipboard.Default.SetTextAsync(Results.CopyText(false)));
    private async void CopyAll(object? s, EventArgs e) => await RunAsync("Copy preview", () => Clipboard.Default.SetTextAsync(Results.CopyText(true)));
    private async void BrowseImport(object? s, EventArgs e) => await RunAsync("Select import file", async () => { var path = await WindowsFiles.OpenAsync(".xlsx", ".xls", ".csv", ".db"); if (path != null) ImportPath.Text = path; });
    private async void BrowseImportFolder(object? s, EventArgs e) => await RunAsync("Select folder", async () => { var path = await WindowsFiles.FolderAsync(); if (path != null) ImportFolder.Text = path; });
    private async void ImportFile(object? s, EventArgs e) => await RunAsync("Import file", async () =>
    {
        string table = SelectedTable(ImportTable), path = ImportPath.Text;
        if (!File.Exists(path)) throw new FileNotFoundException("Select an existing file.");
        await Task.Run(() => { using var access = database.OpenAccess(); DatabaseOperations.Import(path, table, access); });
    });
    private async void ImportDirectory(object? s, EventArgs e) => await RunAsync("Batch import", async () =>
    {
        string table = SelectedTable(ImportTable), folder = ImportFolder.Text;
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Select an existing folder.");
        var progress = new Progress<string>(message => state.Status = message);
        await Task.Run(() =>
        {
            var files = ImportFiles.GetFiles(folder);
            if (files.Count == 0) throw new InvalidOperationException("The folder contains no CSV or XLSX files.");
            using var access = database.OpenAccess();
            for (int i = 0; i < files.Count; i++)
            {
                ((IProgress<string>)progress).Report($"Importing {i + 1}/{files.Count}: {files[i].Name}");
                try { DatabaseOperations.Import(files[i].FullName, table, access); }
                catch (Exception ex) { throw new InvalidOperationException($"Import failed for {files[i].Name}. Previously imported data has been kept: {ex.Message}", ex); }
            }
        });
        state.Status = "Batch import completed.";
    });
    private async void ExportTemplate(object? s, EventArgs e) => await RunAsync("Export template", async () =>
    {
        string table = SelectedTable(ImportTable), type = Type;
        var path = await WindowsFiles.SaveAsync(table + "_template", ".xlsx"); if (path == null) return;
        await Task.Run(() => { using var access = database.OpenAccess(); using var reader = access.GetDataReader(SqlQueryBuilder.BuildSelectAll(table, type) + " where 1=2"); ExcelIO.SaveMiniExcel(path, reader, table); });
    });
    private async void ClearTable(object? s, EventArgs e) => await RunAsync("Clear table", async () =>
    {
        string table = SelectedTable(ImportTable);
        if (!await DisplayAlertAsync("Clear table", $"Delete all data in {table}? This action cannot be undone.", "Clear", "Cancel")) return;
        await Task.Run(() => { using var access = database.OpenAccess(); access.TruncateTable(table); });
        Results.SetTable(null); previewTable?.Dispose(); previewTable = null;
    });
    private void SelectAllExports(object? s, EventArgs e) { foreach (var item in exports) item.Selected = true; }
    private void ClearExports(object? s, EventArgs e) { foreach (var item in exports) item.Selected = false; }
    private void ClearProcedures(object? s, EventArgs e) { foreach (var item in procedures) item.Selected = false; }
    private async Task ExportBatchAsync(string mode) => await RunAsync("Batch export", async () =>
    {
        var names = exports.Where(x => x.Selected).Select(x => x.Name).ToArray();
        if (names.Length == 0) throw new InvalidOperationException("Select the tables or views to export.");
        string type = Type;
        var path = mode == "csv" ? await WindowsFiles.FolderAsync() : await WindowsFiles.SaveAsync(names[0], ".xlsx");
        if (path == null) return;
        var outputs = names.Select(name => Path.Combine(path, WindowsFiles.SafeName(name) + ".csv")).ToArray();
        if (mode == "csv")
        {
            if (outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length) throw new InvalidOperationException("Some table names produce identical file names. Export them separately to different folders.");
            if (outputs.Any(File.Exists) && !await DisplayAlertAsync("Replace CSV files", "The destination contains CSV files with the same names. Replace them?", "Replace", "Cancel")) return;
        }
        await Task.Run(() =>
        {
            using var access = database.OpenAccess();
            if (mode == "epplus") ExcelIO.ExportSheetsWithEpplus(names, path, access, type);
            else if (mode == "mini") ExcelIO.ExportSheetsWithMiniExcel(names, path, access, type);
            else for (int i = 0; i < names.Length; i++) { using var reader = access.GetDataReader(SqlQueryBuilder.BuildSelectAll(names[i], type)); CsvExporter.SaveCsv(reader, outputs[i]); }
        });
    });
    private async void ExportMiniExcel(object? s, EventArgs e) => await ExportBatchAsync("mini");
    private async void ExportEpplus(object? s, EventArgs e) => await ExportBatchAsync("epplus");
    private async void ExportCsvFolder(object? s, EventArgs e) => await ExportBatchAsync("csv");
    private async void RunProcedures(object? s, EventArgs e) => await RunAsync("Run stored procedures", async () =>
    {
        var names = procedures.Where(x => x.Selected).Select(x => x.Name).ToArray();
        if (names.Length == 0) throw new InvalidOperationException("Select stored procedures.");
        if (!await DisplayAlertAsync("Run stored procedures", $"Run {names.Length} procedures in sequence? They may modify data.", "Run", "Cancel")) return;
        var warnings = await Task.Run(() =>
        {
            using var access = database.OpenAccess(); var messages = new List<string>();
            var scripts = access is SQLiteDbAccess ? access.GetProcs() : new List<Proc>();
            foreach (var name in names)
            {
                access.RunProcedure(name);
                var warning = scripts.FirstOrDefault(p => p.Name == name)?.ScriptWarning;
                if (!string.IsNullOrEmpty(warning)) messages.Add(name + ": " + warning);
            }
            return messages;
        });
        if (warnings.Count > 0) { state.Status = "Procedures completed with skipped SQL statements."; await DisplayAlertAsync("Partial execution warning", string.Join(Environment.NewLine, warnings), "OK"); }
    });
    private async void ConvertSqlite(object? s, EventArgs e) => await RunAsync("Convert to SQLite", async () =>
    {
        if (Type != "SQLSERVER") throw new InvalidOperationException("Connect to SQL Server first.");
        string? path = await WindowsFiles.SaveAsync(database.Schema!.Name, ".db"); if (path == null) return;
        SqlServerToSQLite._cancelled = false;
        SqlServerToSQLite.TotalCopyed = 0;
        SqlServerToSQLite.currentProcessTable = null;
        SqlServerToSQLite.Done = false;
        state.Converting = true;
        var timer = Dispatcher.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => state.Status = $"{(SqlServerToSQLite._cancelled ? "Canceling" : "Converting")} · {SqlServerToSQLite.currentProcessTable ?? "Reading schema"} · {SqlServerToSQLite.TotalCopyed:N0} rows copied";
        timer.Start();
        try { var result = await Task.Run(() => database.ConvertToSqlite(path)); timer.Stop(); state.Status = result; await DisplayAlertAsync("Conversion results", state.Status, "OK"); }
        finally { timer.Stop(); state.Converting = false; }
    });
    private void CancelConversion(object? s, EventArgs e) { if (state.Converting) { SqlServerToSQLite._cancelled = true; state.Status = "Cancellation requested. Waiting for the current database operation to finish..."; } }
    private async void About(object? s, EventArgs e) => await DisplayAlertAsync("DataPieMaui", "Windows · .NET MAUI 10\nImport, export, and query SQL Server and SQLite databases.\n\nUses the DataPieCore conversion engine and the original project license.", "OK");
}

public sealed class SelectableObject(string name, string kind) : INotifyPropertyChanged
{
    public string Name { get; } = name;
    public string Kind { get; } = kind;
    private bool selected;
    public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
