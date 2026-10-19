using DataPieCore;
using DBUtil;
using System.Diagnostics;

namespace DataPieMaui.Services.Shared;

public sealed class TableImportOptions(string table, Func<Task<string?>> browse, Func<Task<string?>> browseFolder)
{
    public string Table { get; } = table;
    public string Path { get; set; } = "";
    public string Folder { get; set; } = "";
    public bool Batch { get; set; }
    public bool ValidateLengths { get; set; }
    public async Task BrowseAsync()
    {
        bool batch = Batch;
        var path = await (batch ? browseFolder() : browse());
        if (path != null) { if (batch) Folder = path; else Path = path; }
    }
}

public sealed class TableActionService(WorkspaceContext context)
{
    private string Type => context.Database.Connection!.DatabaseType;
    private TableStruct Resolve(TableStruct table) => TableSelection.Resolve(context.Database.Schema!.Tables, TableSelection.Key(table, Type), Type);

    internal Task ImportIntoAsync(string path, TableStruct table, bool validateLengths) => Task.Run(() =>
    {
        table = Resolve(table);
        using var access = context.Database.OpenAccess();
        DatabaseOperations.Import(path, TableSelection.Key(table, Type), access, validateLengths, table.Name);
    });

    public Task ImportAsync(TableStruct table) => context.Operations.RunAsync("Import file", async watch =>
    {
        table = Resolve(table);
        var options = new TableImportOptions(TableSelection.Label(table, Type), () => context.Desktop.OpenAsync(".xlsx", ".xls", ".csv", ".db"), () => context.Desktop.FolderAsync());
        if (!await context.Dialogs.ShowTableImportAsync(options)) return;
        if (options.Batch)
        {
            await ImportFolderIntoAsync(options.Folder, table, options.ValidateLengths, watch);
            return;
        }
        if (!File.Exists(options.Path)) throw new FileNotFoundException("Select an existing file.", options.Path);
        watch.Start();
        await ImportIntoAsync(options.Path, table, options.ValidateLengths);
    });

    public Task ImportDirectoryAsync(string folder, TableStruct table, bool validateLengths) => context.Operations.RunAsync("Batch import",
        watch => ImportFolderIntoAsync(folder, table, validateLengths, watch));

    private async Task ImportFolderIntoAsync(string folder, TableStruct table, bool validateLengths, Stopwatch watch)
    {
        table = Resolve(table);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Select an existing folder.");
        var files = ImportFiles.GetFiles(folder);
        if (files.Count == 0) throw new InvalidOperationException("The folder contains no CSV or XLSX files.");
        watch.Start();
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            context.State.Status = $"Importing {i + 1}/{files.Count}: {file.Name}";
            try { await ImportIntoAsync(file.FullName, table, validateLengths); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Import failed for {file.Name}. Previously imported data has been kept: {ex.Message}", ex);
            }
        }
        watch.Stop();
        context.State.Status = $"Batch import completed in {watch.Elapsed.TotalSeconds:F1} s.";
    }

    public Task ExportExcelAsync(TableStruct table) => context.Operations.RunAsync("Export data", async watch =>
    {
        table = Resolve(table);
        var path = await context.Desktop.SaveAsync(table.Name, ".xlsx");
        if (path == null) return;
        watch.Start();
        await Task.Run(() =>
        {
            using var access = context.Database.OpenAccess();
            using var reader = access.GetDataReader(TableSelection.SelectAll(table, Type));
            ExcelIO.SaveMiniExcel(path, reader, table.Name);
        });
        watch.Stop();
        context.State.Status = $"Export data completed in {watch.Elapsed.TotalSeconds:F1} s · File: {Path.GetFullPath(path)}";
    });

    public Task AnalyzeAsync(TableStruct table) => context.Operations.RunAsync("Open data analysis", async watch =>
    {
        table = Resolve(table);
        var connection = context.Database.Connection!;
        string label = context.State.Connection;
        watch.Start();
        var source = await Task.Run(() => TableAnalysis.Open(connection.ConnectionString, connection.DatabaseType, table.TableSchemaName, table.Name));
        var session = new DataAnalysisSession(source, label, new AnalysisTemplateStore(context.StorePath));
        await session.RefreshTemplatesAsync();
        await context.Desktop.ShowDataAnalysisAsync(session);
    });

    public Task ClearAsync(TableStruct table) => context.Operations.RunAsync("Clear table", async watch =>
    {
        table = Resolve(table);
        if (!await context.Dialogs.ConfirmAsync("Clear table", $"Delete all data in {TableSelection.Label(table, Type)} from {context.State.Connection}? The table structure will be kept. This action cannot be undone.", "Clear")) return;
        watch.Start();
        await Task.Run(() =>
        {
            using var access = context.Database.OpenAccess();
            access.TruncateTable(TableSelection.Key(table, Type));
        });
        context.Results.Set(null);
        context.QuerySummary = "Run a new preview after clearing the table.";
    });

    public Task DropAsync(TableStruct table) => DropAsync(TableSelection.Key(table, Type));

    public Task DropAsync(string tableKey) => context.Operations.RunAsync("Drop table", async watch =>
    {
        var table = TableSelection.Resolve(context.Database.Schema!.Tables, tableKey, Type);
        string key = TableSelection.Key(table, Type), label = TableSelection.Label(table, Type);
        if (!await context.Dialogs.ConfirmAsync("Drop table", $"Permanently delete table {label} and ALL its data from {context.State.Connection}? This cannot be undone.", "Drop table")) return;
        watch.Start();
        await context.Database.DropTableAsync(key);
        watch.Stop();
        context.Form.DropTable = "";
        if (context.Form.ImportTable == key) context.Form.ImportTable = "";
        if (context.Form.QueryTable == key) context.Form.QueryTable = "";
        context.Results.Set(null);
        context.QuerySummary = "Run a new preview after deleting the table.";
        // Remove stale metadata even if reloading the database schema fails.
        context.Database.Schema!.Tables.Remove(table);
        context.Tables = context.Database.Schema.Tables.Select(t => t.Name).ToArray();
        if (!context.Database.Schema.Tables.Any(t => t.Name == table.Name))
            context.Exports.RemoveAll(x => x.Kind == "Table" && x.Name == table.Name);
        context.State.Status = $"Table {label} deleted in {watch.Elapsed.TotalSeconds:F1} s.";
        context.Notify();
        try { await context.RefreshSchemaAsync(); }
        catch (Exception ex) { await context.Dialogs.AlertAsync("Table deleted", "The table was deleted, but refreshing the schema failed. Reconnect to reload the table list. " + ex.Message); }
    });
}
