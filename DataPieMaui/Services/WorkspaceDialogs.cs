namespace DataPieMaui.Services;

public sealed record WorkspaceDialog(string Title, string Message, string Accept, bool CanCancel, string? Sql = null, Shared.DefinitionEditorSession? Editor = null, Shared.ProcedureExecutionSession? Procedure = null, Shared.TableImportOptions? TableImport = null, string? ChineseTitle = null, string? ChineseMessage = null, string? ChineseAccept = null);

public sealed class WorkspaceDialogs
{
    private TaskCompletionSource<bool>? completion;
    private WorkspaceDialog? unavailable;
    public WorkspaceDialog? Current { get; private set; }
    public event Action? Changed;

    public Task<bool> ConfirmAsync(string title, string message, string accept, string? chineseTitle = null, string? chineseMessage = null, string? chineseAccept = null)
        => ShowAsync(new(title, message, accept, true, ChineseTitle: chineseTitle, ChineseMessage: chineseMessage, ChineseAccept: chineseAccept));
    public Task<bool> ShowTableImportAsync(Shared.TableImportOptions options) => ShowAsync(new("Import file", "", "Import file", true, TableImport: options, ChineseTitle: "导入文件"));
    public Task<bool> AlertAsync(string title, string message, string? chineseTitle = null, string? chineseMessage = null)
        => ShowAsync(new(title, message, "OK", false, ChineseTitle: chineseTitle, ChineseMessage: chineseMessage, ChineseAccept: "确定"));
    public Task<bool> ShowProcedureAsync(Shared.ProcedureExecutionSession session) => ShowAsync(new("Run with parameters", "", "Close", false, Procedure: session, ChineseTitle: "带参数运行"));

    public Task<bool> ShowDefinitionAsync(string title, string sql, string notice, Shared.DefinitionEditorSession? editor = null) => ShowAsync(new(title, notice, "Close", false, sql, editor, ChineseAccept: "关闭"));

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
