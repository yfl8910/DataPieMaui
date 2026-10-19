using System.Diagnostics;
using DataPieCore;
using DBUtil;

namespace DataPieMaui.Services.Shared;

public sealed class QueryPreviewService(WorkspaceContext context) : WorkspacePageActions(context)
{
    public Task PreviewObjectAsync(DefinitionObject target) => RunAsync("Query data", async watch =>
    {
        if (target.Kind != "View" || !Database.Schema!.DefinitionObjects.Any(o => o.Kind == target.Kind && o.Schema == target.Schema && o.Name == target.Name))
            throw new InvalidOperationException("The view is no longer available. Refresh the schema.");
        string name = Type == "SQLSERVER" ? SqlQueryBuilder.QuoteIdentifier(target.Schema, Type) + "." + SqlQueryBuilder.QuoteIdentifier(target.Name, Type)
            : SqlQueryBuilder.QuoteIdentifier(target.Name, Type);
        await LoadAsync("SELECT * FROM " + name, "View · " + target.DisplayName, watch);
    });
    public Task PreviewObjectAsync(string name) => RunAsync("Query data", async watch =>
    {
        Required(name, "Select a table or view.");
        var item = Exports.FirstOrDefault(x => x.Name == name)
            ?? throw new InvalidOperationException("The table or view is not in the current database. Reconnect to refresh the schema.");
        await LoadAsync(SqlQueryBuilder.BuildSelectAll(name, Type), item.Kind + " · " + name, watch);
    });
    // Called inside the shared operation gate; do not acquire the gate a second time.
    internal async Task LoadAsync(string sql, string source, Stopwatch watch)
    {
        watch.Start();
        var result = await Task.Run(() =>
        {
            using var access = Database.OpenAccess();
            var data = QueryPreview.Load(access, sql, 1000);
            return new QueryResultSnapshot(data, State.Connection, source);
        });
        watch.Stop();
        Results.Set(result);
        QuerySummary = $"{source} · {result.RowCount:N0} rows / {result.ColumnCount} columns" + (result.Truncated ? " · First 1,000 rows only. Export to retrieve the full result." : "");
        await Desktop.ShowTablePreviewAsync(Results);
        State.Status = $"Query completed in {watch.Elapsed.TotalSeconds:F1} s: " + QuerySummary;
    }
}
