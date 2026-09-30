using DataPieCore;
using System.Diagnostics;

namespace DataPieMaui.Services.Shared;

/// <summary>Owns conversion progress and cancellation independently of the page lifetime.</summary>
public sealed class DatabaseConversionService(WorkspaceContext context) : WorkspacePageActions(context)
{
    internal async Task ConvertAsync(string path, Stopwatch watch)
    {
        watch.Start();
        SqlServerToSQLite._cancelled = false;
        SqlServerToSQLite.TotalCopyed = 0;
        SqlServerToSQLite.currentProcessTable = null;
        SqlServerToSQLite.Done = false;
        State.Converting = true;
        using var progressCancellation = new CancellationTokenSource();
        var progress = UpdateConversionProgressAsync(progressCancellation.Token);
        string result;
        try
        {
            result = await Task.Run(() => Database.ConvertToSqlite(path));
        }
        finally
        {
            progressCancellation.Cancel();
            await progress;
            watch.Stop();
        }
        State.Converting = false;
        State.Status = $"Conversion completed in {watch.Elapsed.TotalSeconds:F1} s. " + result;
        await Dialogs.AlertAsync("Conversion results", State.Status);
    }
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
}
