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
            string details = ex.ToString();
            Trace.TraceError("{0} failed: {1}", title, details);
            State.Status = title + " failed: " + details;
            await Dialogs.AlertAsync(title + " failed", details);
        }
        finally
        {
            watch.Stop();
            State.Converting = false;
            operationGate.Release();
            State.Busy = false;
        }
    }

}
