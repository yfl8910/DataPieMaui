using System.Diagnostics;
using DataPieCore;
using DBUtil;

namespace DataPieMaui.Services.Shared;

public sealed class QueryPreviewService(WorkspaceContext context) : WorkspacePageActions(context)
{
    private CancellationTokenSource? activeCapture;
    public bool CanCancel => activeCapture is { IsCancellationRequested: false };
    public void Cancel()
    {
        activeCapture?.Cancel();
        Notify();
    }
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
        using var cancellation = new CancellationTokenSource();
        activeCapture = cancellation;
        try
        {
            Notify();
            await CaptureAsync(sql, source, watch, cancellation);
        }
        finally
        {
            activeCapture = null;
            Notify();
        }
    }

    private async Task CaptureAsync(string sql, string source, Stopwatch watch, CancellationTokenSource cancellation)
    {
        string connection = State.Connection;
        var firstPage = new TaskCompletionSource<QueryPreviewResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = Task.Run(() =>
        {
            try
            {
                return Database.CaptureQuery(sql, 1000, data => firstPage.TrySetResult(data), cancellation.Token);
            }
            finally { firstPage.TrySetResult(null); }
        });

        var preview = await firstPage.Task;
        QueryResultSnapshot? pending = null;
        if (preview != null)
        {
            pending = new QueryResultSnapshot(preview, connection, source, cancelCapture: Cancel);
            QuerySummary = $"{source} · Showing the first 1,000 rows · Saving full result…";
            State.SetStatus(QuerySummary, $"{source} · 正在保存完整结果，已显示前 1,000 行…");
            try
            {
                Results.Set(pending);
                await Desktop.ShowTablePreviewAsync(Results);
            }
            catch
            {
                cancellation.Cancel();
                try { (await capture).Archive.Dispose(); }
                catch (Exception ex) { Trace.TraceError("Query capture failed: {0}", ex); }
                if (ReferenceEquals(Results.Current, pending)) Results.Set(new QueryResultSnapshot(preview, connection, source));
                throw;
            }
        }

        QueryResultSnapshot result;
        try
        {
            var (data, archive) = await capture;
            result = new QueryResultSnapshot(data, connection, source, archive: archive);
        }
        catch
        {
            if (preview != null && ReferenceEquals(Results.Current, pending))
            {
                Results.Set(new QueryResultSnapshot(preview, connection, source));
                QuerySummary = $"{source} · Showing the first 1,000 rows · Full result unavailable";
            }
            throw;
        }
        watch.Stop();
        Results.Set(result);
        QuerySummary = $"{source} · {result.TotalRows:N0} rows / {result.ColumnCount} columns" + (result.Truncated ? " · Showing the first 1,000 rows; search covers the full result." : "");
        if (pending == null) await Desktop.ShowTablePreviewAsync(Results);
        string chineseSummary = $"{source} · {result.TotalRows:N0} 行 / {result.ColumnCount} 列";
        if (result.Truncated) chineseSummary += " · 显示前 1,000 行，可搜索完整结果。";
        State.SetStatus($"Query completed in {watch.Elapsed.TotalSeconds:F1} s: " + QuerySummary,
            $"查询完成，耗时 {watch.Elapsed.TotalSeconds:F1} 秒。{chineseSummary}");
    }
}
