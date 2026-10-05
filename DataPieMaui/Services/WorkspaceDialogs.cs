namespace DataPieMaui.Services;

public sealed record WorkspaceDialog(string Title, string Message, string Accept, bool CanCancel, string? Sql = null, Shared.DefinitionEditorSession? Editor = null, Shared.ProcedureExecutionSession? Procedure = null);

public sealed class WorkspaceDialogs
{
    private TaskCompletionSource<bool>? completion;
    private WorkspaceDialog? unavailable;
    public WorkspaceDialog? Current { get; private set; }
    public event Action? Changed;

    public Task<bool> ConfirmAsync(string title, string message, string accept) => ShowAsync(new(title, message, accept, true));
    public Task<bool> AlertAsync(string title, string message) => ShowAsync(new(title, message, "OK", false));
    public Task<bool> ShowProcedureAsync(Shared.ProcedureExecutionSession session) => ShowAsync(new("Run with parameters", "", "Close", false, Procedure: session));

    public Task<bool> ShowDefinitionAsync(string title, string sql, string notice, Shared.DefinitionEditorSession? editor = null) => ShowAsync(new(title, notice, "Close", false, sql, editor));

    private Task<bool> ShowAsync(WorkspaceDialog dialog)
    {
        if (completion != null) throw new InvalidOperationException("Another dialog is already open.");
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        completion = pending;
        Current = dialog;
        if (dialog.Editor is { } editor) editor.BecameIdle += CompleteUnavailable;
        if (dialog.Procedure is { } procedure) procedure.BecameIdle += CompleteUnavailable;
        Changed?.Invoke();
        return pending.Task;
    }

    public void Complete(bool accepted)
    {
        var pending = completion;
        if (Current?.Editor is { } editor) editor.BecameIdle -= CompleteUnavailable;
        if (Current?.Procedure is { } procedure) procedure.BecameIdle -= CompleteUnavailable;
        unavailable = null;
        completion = null;
        Current = null;
        Changed?.Invoke();
        pending?.TrySetResult(accepted);
    }

    // A stale render or a disappearing WebView must not dismiss a newer dialog,
    // or release the operation gate while a database command is still running.
    public bool TryCancelUnavailable(WorkspaceDialog expected)
    {
        if (!ReferenceEquals(Current, expected)) return false;
        if (expected.Editor?.Saving == true || expected.Procedure?.Running == true)
        {
            unavailable = expected;
            return false;
        }
        Complete(false);
        return true;
    }
    private void CompleteUnavailable()
    {
        if (unavailable is { } dialog) TryCancelUnavailable(dialog);
    }
}
