using System.Diagnostics;
using DataPieCore;
using DBUtil;
using DataPieMaui.ViewModels;

namespace DataPieMaui.Services;

public sealed class WorkspaceController
{
    private readonly DatabaseOperations database = new();
    private readonly ConnectionStore store;
    private readonly IDesktopServices desktop;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private bool initialized;
    public WorkspaceState State { get; } = new();
    public WorkspaceForm Form { get; } = new();
    public WorkspaceDialogs Dialogs { get; } = new();
    public QueryResultSession Results { get; } = new();
    public string Section { get; private set; } = "Connections";
    public string QuerySummary { get; private set; } = "Run a preview to open the result table in a separate window.";
    public IReadOnlyList<Dbinfo> Connections { get; private set; } = [];
    public IReadOnlyList<string> Databases { get; private set; } = [];
    public IReadOnlyList<string> Tables { get; private set; } = [];
    public List<SelectableObject> Exports { get; } = [];
    public List<SelectableObject> Procedures { get; } = [];
    public bool IsOperationRunning => operationGate.CurrentCount == 0;
    public bool CanClose => !IsOperationRunning || Dialogs.Current != null;
    public event Action? Changed;

    public WorkspaceController(IDesktopServices desktop, string storePath)
    {
        this.desktop = desktop;
        store = new ConnectionStore(storePath);
        State.PropertyChanged += (_, _) => Notify();
        Dialogs.Changed += Notify;
    }

    private void Notify() => Changed?.Invoke();
    public void Navigate(string section)
    {
        if (IsOperationRunning) return;
        Section = section == "About" || section == "Connections" || State.IsConnected && (section != "Migration" || State.CanConvertToSqlite)
            ? section : "Connections";
        Notify();
    }

    public Task InitializeAsync() => RunAsync("Load connections", async watch =>
    {
        if (initialized) { watch.Start(); return; }
        watch.Start();
        await Task.Run(() =>
        {
            foreach (var path in new[] { Path.Combine(AppContext.BaseDirectory, "data.db"), Path.Combine(Environment.CurrentDirectory, "data.db") }.Distinct(StringComparer.OrdinalIgnoreCase))
                if (File.Exists(path)) store.ImportLegacy(path);
        });
        await ReloadConnectionsAsync();
        initialized = true;
    });

    private async Task RunAsync(string title, Func<Stopwatch, Task> action)
    {
        // Guard the operation itself, including its confirmation, not just the buttons.
        if (!await operationGate.WaitAsync(0)) return;
        var watch = new Stopwatch();
        State.Busy = true;
        State.Status = title + "…";
        try
        {
            await action(watch);
            bool started = watch.IsRunning || watch.ElapsedTicks > 0;
            watch.Stop();
            if (State.Status == title + "…") State.Status = started
                ? $"{title} completed in {watch.Elapsed.TotalSeconds:F1} s" : title + " canceled.";
        }
        catch (OperationCanceledException)
        {
            State.Status = State.Converting ? "Conversion canceled. The output database may be incomplete. Export it again before use." : title + " canceled.";
        }
        catch (ApplicationException ex) when (ex.Message == "User cancelled the conversion")
        {
            State.Status = "Conversion canceled. The output database may be incomplete. Export it again before use.";
        }
        catch (Exception ex)
        {
            watch.Stop();
            State.Status = title + " failed: " + ex.Message;
            await Dialogs.AlertAsync(title + " failed", ex.Message);
        }
        finally { watch.Stop(); State.Converting = false; operationGate.Release(); State.Busy = false; }
    }

    private async Task ReloadConnectionsAsync() => Connections = await Task.Run(store.Load);
    public void SelectConnection(int id)
    {
        if (IsOperationRunning) return;
        Form.SelectedConnectionId = id;
        if (Connections.FirstOrDefault(x => x.Id == id) is { } item)
        {
            Form.ConnectionName = item.Dbname;
            Form.ConnectionType = item.Dbtype;
            Form.ConnectionString = item.ConnectionStrings;
        }
        Notify();
    }
    private Dbinfo ReadConnection() => new() { Id = Form.SelectedConnectionId, Dbname = Form.ConnectionName.Trim(), Dbtype = Form.ConnectionType, ConnectionStrings = Form.ConnectionString };
    private Dbinfo SelectedConnection() => Connections.FirstOrDefault(x => x.Id == Form.SelectedConnectionId) ?? throw new InvalidOperationException("Select a connection.");
    private static DatabaseConnectionInfo Info(Dbinfo record) => new(record.Dbtype, record.ConnectionStrings, record.Dbname);
    private string Type => database.Connection?.DatabaseType ?? throw new InvalidOperationException("Connect to a database first.");
    private static string Required(string value, string message) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value;

    private async Task ConnectAsync(Dbinfo record, bool save)
    {
        await database.ConnectAsync(Info(record));
        State.DatabaseType = database.Connection!.DatabaseType;
        Tables = database.Schema!.Tables.Select(t => t.Name).ToArray();
        Form.QueryTable = Form.ImportTable = Tables.FirstOrDefault() ?? "";
        Form.Sql = Form.ImportPath = Form.ImportFolder = "";
        Results.Set(null);
        QuerySummary = "Run a preview to open the result table in a separate window.";
        Exports.Clear();
        Exports.AddRange(Tables.Select(n => new SelectableObject(n, "Table")));
        Exports.AddRange(database.Schema.ViewNames.Select(n => new SelectableObject(n, "View")));
        Procedures.Clear();
        Procedures.AddRange(database.Schema.Procedures.Select(p => new SelectableObject(p.Name, "Procedure")));
        State.Connection = record.Dbname + " · " + record.Dbtype;
        if (save) { await Task.Run(() => store.Save(record, true)); await ReloadConnectionsAsync(); }
        Section = "Query";
    }

    public Task ConnectSavedAsync() => RunAsync("Connect to database", async w => { w.Start(); await ConnectAsync(SelectedConnection(), false); });
    public Task ConnectEditedAsync() => RunAsync("Connect to database", async w => { w.Start(); await ConnectAsync(ReadConnection(), false); });
    public Task TestConnectionAsync() => RunAsync("Test connection", async w =>
    {
        var info = Info(ReadConnection()); w.Start();
        await Task.Run(() => { using var access = DbAccessFactory.Create(info.ConnectionString, info.DatabaseType); access.conn.Open(); });
    });
    public Task SaveConnectionAsync(bool create) => RunAsync(create ? "Save connection" : "Update connection", async w =>
    {
        var record = ReadConnection();
        if (create) record.Id = 0;
        else if (record.Id <= 0) throw new InvalidOperationException("Select the connection to update.");
        w.Start(); await Task.Run(() => store.Save(record)); await ReloadConnectionsAsync();
    });
    public Task DeleteConnectionAsync() => RunAsync("Delete connection", async w =>
    {
        var item = SelectedConnection();
        if (!await Dialogs.ConfirmAsync("Delete connection", "Delete only the saved connection configuration: " + item.Dbname, "Delete")) return;
        w.Start(); await Task.Run(() => store.Delete(item.Id)); Form.SelectedConnectionId = 0; await ReloadConnectionsAsync();
    });
    public Task ImportConnectionsAsync() => RunAsync("Import legacy connections", async w =>
    {
        var path = await desktop.OpenAsync(".db"); if (path == null) return;
        w.Start(); await Task.Run(() => store.ImportLegacy(path)); await ReloadConnectionsAsync();
    });
    private DBConfig SqlConfig(string name) => new()
    {
        ProviderName = "SQLSERVER", ServerName = Required(Form.Server, "Enter a server address.").Trim(), DataBase = name,
        ValidataType = Form.Authentication, UserName = Form.UserName, UserPwd = Form.Password
    };
    public Task LoadDatabasesAsync() => RunAsync("Load databases", async w =>
    {
        var connection = SqlConfig("master").GetSQLmasterConstring(); w.Start();
        Databases = await Task.Run(() => { using var access = DbAccessFactory.Create(connection, "SQLSERVER"); return access.GetDataBaseInfo().ToArray(); });
        Form.DatabaseName = Databases.FirstOrDefault() ?? "";
    });
    public Task ConnectServerAsync() => RunAsync("Connect to SQL Server", async w =>
    {
        var name = Required(Form.DatabaseName, "Select a database."); w.Start();
        await ConnectAsync(new Dbinfo { Dbname = name, Dbtype = "SQLSERVER", ConnectionStrings = SqlConfig(name).GetConstring() }, true);
    });
    public Task BrowseSqliteAsync() => RunAsync("Select SQLite file", async w =>
    {
        var path = await desktop.OpenAsync(".db", ".sqlite", ".sqlite3"); if (path == null) return;
        w.Start(); Form.SqlitePath = path;
    });
    public Task ConnectSqliteAsync() => RunAsync("Connect to SQLite", async w =>
    {
        if (!File.Exists(Form.SqlitePath)) throw new FileNotFoundException("Select an existing SQLite database.");
        var path = Path.GetFullPath(Form.SqlitePath); w.Start();
        await ConnectAsync(new Dbinfo { Dbname = Path.GetFileNameWithoutExtension(path), Dbtype = "SQLITE", ConnectionStrings = new DBConfig { ProviderName = "SQLITE", DataBase = path }.GetConstring() }, true);
    });

    private void Generate(string kind)
    {
        var name = Required(Form.QueryTable, "Select a table.");
        var table = database.Schema!.Tables.First(t => t.Name == name);
        Form.Sql = kind switch { "UPDATE" => SqlWriter.WriteUpdate(table, Type), "DELETE" => SqlWriter.WriteDelete(table, Type), _ => SqlWriter.WriteSelect(table, Type, 1000) };
    }
    public Task GenerateAsync(string kind) => RunAsync("Generate " + kind, w => { w.Start(); Generate(kind); return Task.CompletedTask; });
    public Task PreviewAsync() => RunAsync("Query", async w =>
    {
        if (string.IsNullOrWhiteSpace(Form.Sql)) Generate("SELECT");
        await LoadPreviewAsync(Form.Sql, "SQL query", w);
    });
    public Task PreviewImportTableAsync() => PreviewObjectAsync(Form.ImportTable);
    public Task PreviewObjectAsync(string name) => RunAsync("Query data", async w =>
    {
        Required(name, "Select a table or view.");
        var item = Exports.FirstOrDefault(x => x.Name == name)
            ?? throw new InvalidOperationException("The table or view is not in the current database. Reconnect to refresh the schema.");
        await LoadPreviewAsync(SqlQueryBuilder.BuildSelectAll(name, Type), item.Kind + " · " + name, w);
    });
    private async Task LoadPreviewAsync(string sql, string source, Stopwatch w)
    {
        w.Start();
        var result = await Task.Run(() => { using var access = database.OpenAccess(); using var table = QueryPreview.Load(access, sql, 1000, out bool truncated); return new QueryResultSnapshot(table, truncated, State.Connection, source); });
        w.Stop(); Results.Set(result);
        QuerySummary = $"{source} · {result.Rows.Length:N0} rows / {result.Columns.Length} columns" + (result.Truncated ? " · First 1,000 rows only. Export to retrieve the full result." : "");
        await desktop.ShowResultsAsync(Results);
        State.Status = $"Query completed in {w.Elapsed.TotalSeconds:F1} s: " + QuerySummary;
    }
    public Task OpenResultsAsync() => RunAsync("Open results", async w => { w.Start(); if (Results.Current != null) await desktop.ShowResultsAsync(Results); });
    public Task ExecuteSqlAsync() => RunAsync("Run SQL", async w =>
    {
        var sql = Required(Form.Sql, "Enter SQL.");
        if (!await Dialogs.ConfirmAsync("Run SQL", "Run the SQL in the editor against the current database?", "Run")) return;
        w.Start();
        int count = await Task.Run(() => { using var access = database.OpenAccess(); return access.ExecuteSql(sql); });
        w.Stop(); State.Status = $"SQL completed in {w.Elapsed.TotalSeconds:F1} s: {count:N0} rows affected";
    });
    public Task ExportQueryAsync(bool csv, bool wholeTable) => RunAsync("Export data", async w =>
    {
        string name = wholeTable ? Required(Form.QueryTable, "Select a table.") : "Query";
        string sql = wholeTable ? SqlQueryBuilder.BuildSelectAll(name, Type) : Required(Form.Sql, "Enter SQL.");
        var path = await desktop.SaveAsync(name, csv ? ".csv" : ".xlsx"); if (path == null) return;
        w.Start();
        await Task.Run(() => { using var access = database.OpenAccess(); using var reader = access.GetDataReader(sql); if (csv) CsvExporter.SaveCsv(reader, path); else ExcelIO.SaveMiniExcel(path, reader, name); });
    });
    public Task BrowseImportAsync(bool folder) => RunAsync(folder ? "Select folder" : "Select import file", async w =>
    {
        var path = folder ? await desktop.FolderAsync() : await desktop.OpenAsync(".xlsx", ".xls", ".csv", ".db"); if (path == null) return;
        w.Start(); if (folder) Form.ImportFolder = path; else Form.ImportPath = path;
    });
    public Task ImportFileAsync() => RunAsync("Import file", async w =>
    {
        string table = Required(Form.ImportTable, "Select a table."), path = Form.ImportPath;
        if (!File.Exists(path)) throw new FileNotFoundException("Select an existing file.");
        w.Start(); await Task.Run(() => { using var access = database.OpenAccess(); DatabaseOperations.Import(path, table, access); });
    });
    public Task ImportDirectoryAsync() => RunAsync("Batch import", async w =>
    {
        string table = Required(Form.ImportTable, "Select a table."), folder = Form.ImportFolder;
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Select an existing folder.");
        var files = ImportFiles.GetFiles(folder);
        if (files.Count == 0) throw new InvalidOperationException("The folder contains no CSV or XLSX files.");
        w.Start();
        // Await each file so progress cannot arrive after the completion message.
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i]; State.Status = $"Importing {i + 1}/{files.Count}: {file.Name}";
            try { await Task.Run(() => { using var access = database.OpenAccess(); DatabaseOperations.Import(file.FullName, table, access); }); }
            catch (Exception ex) { throw new InvalidOperationException($"Import failed for {file.Name}. Previously imported data has been kept: {ex.Message}", ex); }
        }
        w.Stop(); State.Status = $"Batch import completed in {w.Elapsed.TotalSeconds:F1} s.";
    });
    public Task ExportTemplateAsync() => RunAsync("Export template", async w =>
    {
        string table = Required(Form.ImportTable, "Select a table."), type = Type;
        var path = await desktop.SaveAsync(table + "_template", ".xlsx"); if (path == null) return;
        w.Start(); await Task.Run(() => { using var access = database.OpenAccess(); using var reader = access.GetDataReader(SqlQueryBuilder.BuildSelectAll(table, type) + " where 1=2"); ExcelIO.SaveMiniExcel(path, reader, table); });
    });
    public Task ClearTableAsync() => RunAsync("Clear table", async w =>
    {
        string table = Required(Form.ImportTable, "Select a table.");
        if (!await Dialogs.ConfirmAsync("Clear table", $"Delete all data in {table}? This action cannot be undone.", "Clear")) return;
        w.Start(); await Task.Run(() => { using var access = database.OpenAccess(); access.TruncateTable(table); });
        Results.Set(null); QuerySummary = "Run a new preview after clearing the table.";
    });
    public void SelectAll(bool selected, bool procedures = false)
    {
        if (IsOperationRunning) return;
        foreach (var item in procedures ? Procedures : Exports) item.Selected = selected;
        Notify();
    }
    public Task ExportBatchAsync(string mode) => RunAsync("Batch export", async w =>
    {
        var names = Exports.Where(x => x.Selected).Select(x => x.Name).ToArray();
        if (names.Length == 0) throw new InvalidOperationException("Select the tables or views to export.");
        string type = Type;
        var path = mode == "csv" ? await desktop.FolderAsync() : await desktop.SaveAsync(names[0], ".xlsx"); if (path == null) return;
        var outputs = names.Select(name => Path.Combine(path, SafeFileName(name) + ".csv")).ToArray();
        if (mode == "csv")
        {
            if (outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length) throw new InvalidOperationException("Some table names produce identical file names. Export them separately to different folders.");
            if (outputs.Any(File.Exists) && !await Dialogs.ConfirmAsync("Replace CSV files", "The destination contains CSV files with the same names. Replace them?", "Replace")) return;
        }
        w.Start(); await Task.Run(() =>
        {
            using var access = database.OpenAccess();
            if (mode == "epplus") ExcelIO.ExportSheetsWithEpplus(names, path, access, type);
            else if (mode == "mini") ExcelIO.ExportSheetsWithMiniExcel(names, path, access, type);
            else for (int i = 0; i < names.Length; i++) { using var reader = access.GetDataReader(SqlQueryBuilder.BuildSelectAll(names[i], type)); CsvExporter.SaveCsv(reader, outputs[i]); }
        });
    });
    public static string SafeFileName(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(safe) ? "export" : safe;
    }
    public Task RunProceduresAsync() => RunAsync("Run stored procedures", async w =>
    {
        var names = Procedures.Where(x => x.Selected).Select(x => x.Name).ToArray();
        if (names.Length == 0) throw new InvalidOperationException("Select stored procedures.");
        if (!await Dialogs.ConfirmAsync("Run stored procedures", $"Run {names.Length} procedures in sequence? They may modify data.", "Run")) return;
        w.Start();
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
        w.Stop();
        if (warnings.Count > 0)
        {
            State.Status = $"Procedures completed in {w.Elapsed.TotalSeconds:F1} s with skipped SQL statements.";
            await Dialogs.AlertAsync("Partial execution warning", string.Join(Environment.NewLine, warnings));
        }
    });
    public Task ConvertSqliteAsync() => RunAsync("Convert to SQLite", async w =>
    {
        if (Type != "SQLSERVER") throw new InvalidOperationException("Connect to SQL Server first.");
        var path = await desktop.SaveAsync(database.Schema!.Name, ".db"); if (path == null) return;
        w.Start(); SqlServerToSQLite._cancelled = false; SqlServerToSQLite.TotalCopyed = 0;
        SqlServerToSQLite.currentProcessTable = null; SqlServerToSQLite.Done = false;
        State.Converting = true;
        using var progressCancellation = new CancellationTokenSource();
        var progress = UpdateConversionProgressAsync(progressCancellation.Token);
        string result;
        try { result = await Task.Run(() => database.ConvertToSqlite(path)); }
        finally
        {
            progressCancellation.Cancel();
            await progress;
            w.Stop();
        }
        State.Converting = false;
        State.Status = $"Conversion completed in {w.Elapsed.TotalSeconds:F1} s. " + result;
        await Dialogs.AlertAsync("Conversion results", State.Status);
    });
    private async Task UpdateConversionProgressAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(token))
                State.Status = $"{(SqlServerToSQLite._cancelled ? "Canceling" : "Converting")} · {SqlServerToSQLite.currentProcessTable ?? "Reading schema"} · {SqlServerToSQLite.TotalCopyed:N0} rows copied";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public void CancelConversion()
    {
        if (!State.Converting) return;
        SqlServerToSQLite._cancelled = true;
        State.Status = "Cancellation requested. Waiting for the current database operation to finish…";
    }
    public void NotifyCloseBlocked()
    {
        if (State.Converting) CancelConversion();
        else State.Status = "An operation is running. Please wait until it finishes before closing the window.";
    }
}
