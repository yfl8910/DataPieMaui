using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.ServiceProcess;
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
        await RunAsync("读取连接配置", async () =>
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
        else state.Status = "任务正在执行，请等待完成后再关闭窗口。";
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
            if (state.Status == title + "…") state.Status = $"{title}完成 · {watch.Elapsed.TotalSeconds:F1} 秒";
        }
        catch (OperationCanceledException)
        {
            state.Status = "转换已取消。输出数据库可能不完整，请重新导出后再使用。";
        }
        catch (ApplicationException ex) when (SqlServerToSQLite._cancelled && ex.Message == "User cancelled the conversion")
        {
            state.Status = "转换已取消。输出数据库可能不完整，请重新导出后再使用。";
        }
        catch (Exception ex)
        {
            state.Status = title + "失败：" + ex.Message;
            await DisplayAlertAsync(title + "失败", ex.Message, "确定");
        }
        finally { state.Busy = false; }
    }

    private void Navigate(object? sender, EventArgs e) => ShowPanel((string)((Button)sender!).CommandParameter);
    private void ShowPanel(string name)
    {
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
        exports.AddRange(names.Select(n => new SelectableObject(n, "表")));
        exports.AddRange(database.Schema.ViewNames.Select(n => new SelectableObject(n, "视图")));
        procedures.Clear();
        procedures.AddRange(database.Schema.Procedures.Select(p => new SelectableObject(p.Name, "过程")));
        ExportObjects.ItemsSource = null; ExportObjects.ItemsSource = exports;
        ProcedureObjects.ItemsSource = null; ProcedureObjects.ItemsSource = procedures;
    }
    private async void ConnectSaved(object? s, EventArgs e) => await RunAsync("连接数据库", () => ConnectAsync(SavedConnections.SelectedItem as Dbinfo ?? throw new InvalidOperationException("请选择连接。"), false));
    private async void ConnectEdited(object? s, EventArgs e) => await RunAsync("连接数据库", () => ConnectAsync(ReadConnection(), false));
    private async void TestConnection(object? s, EventArgs e) => await RunAsync("测试连接", async () =>
    {
        var info = Info(ReadConnection());
        await Task.Run(() => { using var access = DbAccessFactory.Create(info.ConnectionString, info.DatabaseType); access.conn.Open(); });
    });
    private async void AddConnection(object? s, EventArgs e) => await RunAsync("保存连接", () => { var record = ReadConnection(); record.Id = 0; store.Save(record); ReloadConnections(); return Task.CompletedTask; });
    private async void UpdateConnection(object? s, EventArgs e) => await RunAsync("更新连接", () =>
    {
        if (selectedConnectionId <= 0) throw new InvalidOperationException("请选择需要更新的连接。");
        store.Save(ReadConnection()); ReloadConnections(); return Task.CompletedTask;
    });
    private async void DeleteConnection(object? s, EventArgs e) => await RunAsync("删除连接", async () =>
    {
        if (SavedConnections.SelectedItem is not Dbinfo item) throw new InvalidOperationException("请选择连接。");
        if (!await DisplayAlertAsync("删除连接", "仅删除保存的连接配置：" + item.Dbname, "删除", "取消")) return;
        store.Delete(item.Id); selectedConnectionId = 0; ReloadConnections();
    });
    private async void ImportConnections(object? s, EventArgs e) => await RunAsync("导入旧连接配置", async () =>
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
        if (string.IsNullOrWhiteSpace(Server.Text)) throw new InvalidOperationException("请输入服务器地址。");
        return new DBConfig { ProviderName = "SQLSERVER", ServerName = Server.Text.Trim(), DataBase = name, ValidataType = (string?)Authentication.SelectedItem ?? "Windows", UserName = UserName.Text, UserPwd = Password.Text };
    }
    private async void LoadDatabases(object? s, EventArgs e) => await RunAsync("读取数据库列表", async () =>
    {
        var connection = SqlConfig("master").GetSQLmasterConstring();
        Databases.ItemsSource = await Task.Run(() => { using var access = DbAccessFactory.Create(connection, "SQLSERVER"); return access.GetDataBaseInfo(); });
        Databases.SelectedIndex = Databases.Items.Count > 0 ? 0 : -1;
    });
    private async void ConnectServer(object? s, EventArgs e) => await RunAsync("连接 SQL Server", () =>
    {
        var name = Databases.SelectedItem as string ?? throw new InvalidOperationException("请选择数据库。");
        return ConnectAsync(new Dbinfo { Dbname = name, Dbtype = "SQLSERVER", ConnectionStrings = SqlConfig(name).GetConstring() }, true);
    });
    private async void BrowseSqlite(object? s, EventArgs e) => await RunAsync("选择 SQLite", async () => { var path = await WindowsFiles.OpenAsync(".db", ".sqlite", ".sqlite3"); if (path != null) SqlitePath.Text = path; });
    private async void ConnectSqlite(object? s, EventArgs e) => await RunAsync("连接 SQLite", () =>
    {
        if (!File.Exists(SqlitePath.Text)) throw new FileNotFoundException("请选择已存在的 SQLite 数据库。");
        var path = Path.GetFullPath(SqlitePath.Text);
        return ConnectAsync(new Dbinfo { Dbname = Path.GetFileNameWithoutExtension(path), Dbtype = "SQLITE", ConnectionStrings = new DBConfig { ProviderName = "SQLITE", DataBase = path }.GetConstring() }, true);
    });
    private async Task ServiceAsync(bool start) => await RunAsync(start ? "启动本机服务" : "停止本机服务", async () =>
    {
        if (!start && !await DisplayAlertAsync("停止 SQL Server", "停止本机 MSSQLSERVER 服务会断开其他应用的连接。", "停止", "取消")) return;
        await Task.Run(() =>
        {
            using var service = new ServiceController("MSSQLSERVER", Environment.MachineName);
            var target = start ? ServiceControllerStatus.Running : ServiceControllerStatus.Stopped;
            if (service.Status == target) return;
            if (start && service.Status != ServiceControllerStatus.StartPending) service.Start();
            if (!start && service.Status != ServiceControllerStatus.StopPending) service.Stop();
            service.WaitForStatus(target, TimeSpan.FromSeconds(30));
        });
    });
    private async void StartService(object? s, EventArgs e) => await ServiceAsync(true);
    private async void StopService(object? s, EventArgs e) => await ServiceAsync(false);
    private string Type => database.Connection?.DatabaseType ?? throw new InvalidOperationException("请先连接数据库。");
    private static string SelectedTable(Picker picker) => picker.SelectedItem as string ?? throw new InvalidOperationException("请选择数据表。");
    private void Generate(string kind)
    {
        var name = SelectedTable(QueryTable);
        var table = database.Schema!.Tables.First(t => t.Name == name);
        SqlEditor.Text = kind switch { "UPDATE" => SqlWriter.WriteUpdate(table, Type), "DELETE" => SqlWriter.WriteDelete(table, Type), _ => SqlWriter.WriteSelect(table, Type, 1000) };
    }
    private async void GenerateSelect(object? s, EventArgs e) => await RunAsync("生成 SELECT", () => { Generate("SELECT"); return Task.CompletedTask; });
    private async void GenerateUpdate(object? s, EventArgs e) => await RunAsync("生成 UPDATE", () => { Generate("UPDATE"); return Task.CompletedTask; });
    private async void GenerateDelete(object? s, EventArgs e) => await RunAsync("生成 DELETE", () => { Generate("DELETE"); return Task.CompletedTask; });
    private string QuerySql()
    {
        if (string.IsNullOrWhiteSpace(SqlEditor.Text)) throw new InvalidOperationException("请输入 SQL。");
        return SqlEditor.Text;
    }
    private async void Preview(object? s, EventArgs e) => await RunAsync("查询", async () =>
    {
        if (string.IsNullOrWhiteSpace(SqlEditor.Text)) Generate("SELECT");
        var sql = QuerySql();
        var result = await Task.Run(() => { using var access = database.OpenAccess(); var table = QueryPreview.Load(access, sql, 1000, out bool truncated); return (table, truncated); });
        Results.SetTable(result.table);
        previewTable?.Dispose(); previewTable = result.table;
        state.Status = $"查询完成 · {result.table.Rows.Count:N0} 行 / {result.table.Columns.Count} 列" + (result.truncated ? " · 仅显示前 1,000 行，导出可获取完整结果。" : "");
    });
    private async void ExecuteSql(object? s, EventArgs e) => await RunAsync("执行 SQL", async () =>
    {
        var sql = QuerySql();
        if (!await DisplayAlertAsync("执行 SQL", "将对当前数据库执行编辑器中的 SQL，是否继续？", "执行", "取消")) return;
        int count = await Task.Run(() => { using var access = database.OpenAccess(); return access.ExecuteSql(sql); });
        state.Status = $"SQL 执行完成 · 影响 {count:N0} 行";
    });
    private async Task ExportQueryAsync(bool csv, bool wholeTable) => await RunAsync("导出数据", async () =>
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
    private async void CopyRow(object? s, EventArgs e) => await RunAsync("复制选中行", () => Clipboard.Default.SetTextAsync(Results.CopyText(false)));
    private async void CopyAll(object? s, EventArgs e) => await RunAsync("复制预览", () => Clipboard.Default.SetTextAsync(Results.CopyText(true)));
    private async void BrowseImport(object? s, EventArgs e) => await RunAsync("选择导入文件", async () => { var path = await WindowsFiles.OpenAsync(".xlsx", ".xls", ".csv", ".db"); if (path != null) ImportPath.Text = path; });
    private async void BrowseImportFolder(object? s, EventArgs e) => await RunAsync("选择文件夹", async () => { var path = await WindowsFiles.FolderAsync(); if (path != null) ImportFolder.Text = path; });
    private async void ImportFile(object? s, EventArgs e) => await RunAsync("导入文件", async () =>
    {
        string table = SelectedTable(ImportTable), path = ImportPath.Text;
        if (!File.Exists(path)) throw new FileNotFoundException("请选择已存在的文件。");
        await Task.Run(() => { using var access = database.OpenAccess(); DatabaseOperations.Import(path, table, access); });
    });
    private async void ImportDirectory(object? s, EventArgs e) => await RunAsync("批量导入", async () =>
    {
        string table = SelectedTable(ImportTable), folder = ImportFolder.Text;
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("请选择已存在的文件夹。");
        var progress = new Progress<string>(message => state.Status = message);
        await Task.Run(() =>
        {
            var files = ImportFiles.GetFiles(folder);
            if (files.Count == 0) throw new InvalidOperationException("文件夹中没有 CSV 或 XLSX 文件。");
            using var access = database.OpenAccess();
            for (int i = 0; i < files.Count; i++)
            {
                ((IProgress<string>)progress).Report($"正在导入 {i + 1}/{files.Count}：{files[i].Name}");
                try { DatabaseOperations.Import(files[i].FullName, table, access); }
                catch (Exception ex) { throw new InvalidOperationException($"文件 {files[i].Name} 导入失败，之前已导入的数据将保留：{ex.Message}", ex); }
            }
        });
        state.Status = "批量导入完成。";
    });
    private async void ExportTemplate(object? s, EventArgs e) => await RunAsync("导出模板", async () =>
    {
        string table = SelectedTable(ImportTable), type = Type;
        var path = await WindowsFiles.SaveAsync(table + "_template", ".xlsx"); if (path == null) return;
        await Task.Run(() => { using var access = database.OpenAccess(); using var reader = access.GetDataReader(SqlQueryBuilder.BuildSelectAll(table, type) + " where 1=2"); ExcelIO.SaveMiniExcel(path, reader, table); });
    });
    private async void ClearTable(object? s, EventArgs e) => await RunAsync("清空数据表", async () =>
    {
        string table = SelectedTable(ImportTable);
        if (!await DisplayAlertAsync("清空数据表", $"将删除 {table} 中的全部数据。此操作不可撤销。", "清空", "取消")) return;
        await Task.Run(() => { using var access = database.OpenAccess(); access.TruncateTable(table); });
        Results.SetTable(null); previewTable?.Dispose(); previewTable = null;
    });
    private void SelectAllExports(object? s, EventArgs e) { foreach (var item in exports) item.Selected = true; }
    private void ClearExports(object? s, EventArgs e) { foreach (var item in exports) item.Selected = false; }
    private void ClearProcedures(object? s, EventArgs e) { foreach (var item in procedures) item.Selected = false; }
    private async Task ExportBatchAsync(string mode) => await RunAsync("批量导出", async () =>
    {
        var names = exports.Where(x => x.Selected).Select(x => x.Name).ToArray();
        if (names.Length == 0) throw new InvalidOperationException("请勾选需要导出的表或视图。");
        string type = Type;
        var path = mode == "csv" ? await WindowsFiles.FolderAsync() : await WindowsFiles.SaveAsync(names[0], ".xlsx");
        if (path == null) return;
        var outputs = names.Select(name => Path.Combine(path, WindowsFiles.SafeName(name) + ".csv")).ToArray();
        if (mode == "csv")
        {
            if (outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length) throw new InvalidOperationException("部分表名转换为文件名后重复，请分批导出到不同文件夹。");
            if (outputs.Any(File.Exists) && !await DisplayAlertAsync("覆盖 CSV", "目标文件夹包含同名 CSV 文件，是否覆盖？", "覆盖", "取消")) return;
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
    private async void RunProcedures(object? s, EventArgs e) => await RunAsync("执行存储过程", async () =>
    {
        var names = procedures.Where(x => x.Selected).Select(x => x.Name).ToArray();
        if (names.Length == 0) throw new InvalidOperationException("请勾选存储过程。");
        if (!await DisplayAlertAsync("执行存储过程", $"将依次运行 {names.Length} 个过程，过程可能修改数据。", "执行", "取消")) return;
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
        if (warnings.Count > 0) { state.Status = "过程执行完成，存在跳过的 SQL。"; await DisplayAlertAsync("部分执行警告", string.Join(Environment.NewLine, warnings), "确定"); }
    });
    private async void ConvertSqlite(object? s, EventArgs e) => await RunAsync("转换 SQLite", async () =>
    {
        if (Type != "SQLSERVER") throw new InvalidOperationException("请先连接 SQL Server。");
        string? path = await WindowsFiles.SaveAsync(database.Schema!.Name, ".db"); if (path == null) return;
        SqlServerToSQLite._cancelled = false;
        SqlServerToSQLite.TotalCopyed = 0;
        SqlServerToSQLite.currentProcessTable = null;
        SqlServerToSQLite.Done = false;
        state.Converting = true;
        var timer = Dispatcher.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => state.Status = $"{(SqlServerToSQLite._cancelled ? "正在取消" : "正在转换")} · {SqlServerToSQLite.currentProcessTable ?? "读取结构"} · 已复制 {SqlServerToSQLite.TotalCopyed:N0} 行";
        timer.Start();
        try { var result = await Task.Run(() => database.ConvertToSqlite(path)); timer.Stop(); state.Status = result; await DisplayAlertAsync("转换结果", state.Status, "确定"); }
        finally { timer.Stop(); state.Converting = false; }
    });
    private void CancelConversion(object? s, EventArgs e) { if (state.Converting) { SqlServerToSQLite._cancelled = true; state.Status = "已请求取消，正在等待当前数据库操作结束…"; } }
    private async void About(object? s, EventArgs e) => await DisplayAlertAsync("DataPieMaui", "Windows · .NET MAUI 10\nSQL Server / SQLite 数据导入、导出及查询工具。\n\n保留 DataPieCore 转换引擎与原项目许可证。", "确定");
}

public sealed class SelectableObject(string name, string kind) : INotifyPropertyChanged
{
    public string Name { get; } = name;
    public string Kind { get; } = kind;
    private bool selected;
    public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
