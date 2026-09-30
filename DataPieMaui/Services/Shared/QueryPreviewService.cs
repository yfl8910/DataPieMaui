using System.Diagnostics;
using DataPieCore;

namespace DataPieMaui.Services.Shared;

public sealed class QueryPreviewService(WorkspaceContext context) : WorkspacePageActions(context)
{
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
        await Desktop.ShowResultsAsync(Results);
        State.Status = $"Query completed in {watch.Elapsed.TotalSeconds:F1} s: " + QuerySummary;
    }
}
