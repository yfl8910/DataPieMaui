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
        string english = $"Conversion completed in {watch.Elapsed.TotalSeconds:F1} s. " + result;
        string chinese = $"转换完成，耗时 {watch.Elapsed.TotalSeconds:F1} 秒。 " + result;
        State.SetStatus(english, chinese);
        await Dialogs.AlertAsync("Conversion results", english, "转换结果", chinese);
        State.SetStatus(english + " · File: " + Path.GetFullPath(path), chinese + " · 文件：" + Path.GetFullPath(path));
    }
    private async Task UpdateConversionProgressAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(token))
                State.SetStatus($"{(SqlServerToSQLite._cancelled ? "Canceling" : "Converting")} · {SqlServerToSQLite.currentProcessTable ?? "Reading schema"} · {SqlServerToSQLite.TotalCopyed:N0} rows copied",
                    $"{(SqlServerToSQLite._cancelled ? "正在取消" : "正在转换")} · {SqlServerToSQLite.currentProcessTable ?? "读取数据库结构"} · 已复制 {SqlServerToSQLite.TotalCopyed:N0} 行");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public void CancelConversion()
    {
        if (!State.Converting) return;
        SqlServerToSQLite._cancelled = true;
        State.SetStatus("Cancellation requested. Waiting for the current database operation to finish…", "已请求取消，正在等待当前数据库操作结束…");
    }
}
