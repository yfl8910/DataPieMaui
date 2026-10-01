namespace DataPieMaui.Services;

public sealed record WorkspaceDialog(string Title, string Message, string Accept, bool CanCancel, string? Sql = null, Shared.DefinitionEditorSession? Editor = null);

public sealed class WorkspaceDialogs
{
    private TaskCompletionSource<bool>? completion;
    public WorkspaceDialog? Current { get; private set; }
    public event Action? Changed;

    public Task<bool> ConfirmAsync(string title, string message, string accept) => ShowAsync(new(title, message, accept, true));
    public Task<bool> AlertAsync(string title, string message) => ShowAsync(new(title, message, "OK", false));

    public Task<bool> ShowDefinitionAsync(string title, string sql, string notice, Shared.DefinitionEditorSession? editor = null) => ShowAsync(new(title, notice, "Close", false, sql, editor));

    private Task<bool> ShowAsync(WorkspaceDialog dialog)
    {
        if (completion != null) throw new InvalidOperationException("Another dialog is already open.");
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        completion = pending;
        Current = dialog;
        Changed?.Invoke();
        return pending.Task;
    }

    public void Complete(bool accepted)
    {
        var pending = completion;
        completion = null;
        Current = null;
        Changed?.Invoke();
        pending?.TrySetResult(accepted);
    }
}
