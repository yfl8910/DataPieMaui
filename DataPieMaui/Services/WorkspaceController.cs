using DataPieMaui.Services.Shared;

namespace DataPieMaui.Services;

/// <summary>Composes the workspace and coordinates navigation and native-window lifecycle.</summary>
public sealed class WorkspaceController
{
    public WorkspaceContext Context { get; }
    public DatabaseConversionService Conversion { get; }
    public bool IsOperationRunning => Context.Operations.IsRunning;
    public bool CanClose => !IsOperationRunning || Context.Dialogs.Current != null;
    public event Action? Changed
    {
        add => Context.Changed += value;
        remove => Context.Changed -= value;
    }

    public WorkspaceController(IDesktopServices desktop, string storePath)
    {
        Context = new WorkspaceContext(desktop, storePath);
        Conversion = new DatabaseConversionService(Context);
    }

    public void Navigate(string section)
    {
        if (IsOperationRunning) return;
        Context.Section = section == "About" || section == "Connections" || Context.State.IsConnected && (section != "Migration" || Context.State.CanConvertToSqlite)
            ? section : "Connections";
        Context.Notify();
    }


    public void NotifyCloseBlocked()
    {
        if (Context.State.Converting) Conversion.CancelConversion();
        else Context.State.Status = "An operation is running. Please wait until it finishes before closing the window.";
    }
}
