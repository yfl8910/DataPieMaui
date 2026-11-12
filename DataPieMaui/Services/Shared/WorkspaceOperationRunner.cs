using System.Diagnostics;
using DataPieMaui.ViewModels;

namespace DataPieMaui.Services.Shared;

public sealed class WorkspaceOperationRunner(WorkspaceState State, WorkspaceDialogs Dialogs)
{
    private readonly SemaphoreSlim operationGate = new(1, 1);
    public bool IsRunning => operationGate.CurrentCount == 0;

    public async Task RunAsync(string title, Func<Stopwatch, Task> action)
    {
        // Guard the operation itself, including its confirmation, not just the buttons.
        if (!await operationGate.WaitAsync(0)) return;
        var watch = new Stopwatch();
        State.Busy = true;
        State.SetStatus(title + "…", "正在处理…");
        int initialStatusVersion = State.StatusVersion;
        try
        {
            await action(watch);
            bool started = watch.IsRunning || watch.ElapsedTicks > 0;
            watch.Stop();
            if (State.StatusVersion == initialStatusVersion)
                State.SetStatus(started ? $"{title} completed in {watch.Elapsed.TotalSeconds:F1} s" : title + " canceled.",
                    started ? $"操作已完成，耗时 {watch.Elapsed.TotalSeconds:F1} 秒。" : "操作已取消。");
        }
        catch (OperationCanceledException)
        {
            if (State.Converting) SetConversionCanceled();
            else State.SetStatus(title + " canceled.", "操作已取消。");
        }
        catch (ApplicationException ex) when (ex.Message == "User cancelled the conversion")
        {
            SetConversionCanceled();
        }
        catch (Exception ex)
        {
            watch.Stop();
            Trace.TraceError("{0} failed: {1}", title, ex);
            State.SetStatus(title + " failed.", "操作失败，请查看提示。");
            await Dialogs.AlertAsync(title + " failed", ex.Message, "操作失败", (ex as BatchImportException)?.ChineseMessage);
        }
        finally
        {
            watch.Stop();
            State.Converting = false;
            operationGate.Release();
            State.Busy = false;
        }
    }

    private void SetConversionCanceled() => State.SetStatus(
        "Conversion canceled. The output database may be incomplete. Export it again before use.",
        "转换已取消。输出数据库可能不完整，请重新导出后再使用。");
}
