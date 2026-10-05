using DataPieMaui.Services;
using DataPieMaui.Services.Shared;
using DataPieMaui.ViewModels;
using DBUtil;

internal static class DialogLifecycleChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Unavailable confirmations cancel without dismissing a newer dialog", async () =>
        {
            var dialogs = new WorkspaceDialogs();
            var first = dialogs.ConfirmAsync("First", "", "Run");
            var old = dialogs.Current!;
            Check(dialogs.TryCancelUnavailable(old) && !await first, "Unavailable confirmation was accepted or left pending");
            var second = dialogs.ConfirmAsync("Second", "", "Run");
            Check(!dialogs.TryCancelUnavailable(old) && !second.IsCompleted, "Stale render dismissed a new dialog");
            dialogs.Complete(false); await second;
        });
        await test("Unavailable procedure dialog keeps the operation gate until execution finishes", async () =>
        {
            var dialogs = new WorkspaceDialogs();
            var state = new WorkspaceState();
            var runner = new WorkspaceOperationRunner(state, dialogs);
            var command = new TaskCompletionSource<ProcedureExecutionResult>();
            var session = new ProcedureExecutionSession("db", new("Procedure", "dbo", "Run"), [], _ => command.Task);
            var operation = runner.RunAsync("Procedure", async _ => { await dialogs.ShowProcedureAsync(session); });
            session.Review(); var execution = session.ConfirmAsync();
            Check(!dialogs.TryCancelUnavailable(dialogs.Current!) && runner.IsRunning && !operation.IsCompleted, "Gate released during execution");
            command.SetResult(new(1, 0, new Dictionary<string, object>()));
            await execution; await operation.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!runner.IsRunning && session.Result?.RowsAffected == 1, "Lost result or operation gate not released");
        });
        await test("Unavailable definition dialog waits for save failure and preserves the draft", async () =>
        {
            var dialogs = new WorkspaceDialogs();
            var original = new ObjectDefinition("CREATE VIEW dbo.V AS SELECT 1 AS Id") { CanEdit = true };
            var completion = new TaskCompletionSource<ObjectDefinition>();
            var editor = new DefinitionEditorSession(new("View", "dbo", "V"), original, "db", (_, _) => completion.Task);
            var pending = dialogs.ShowDefinitionAsync("V", original.Sql, "", editor);
            editor.BeginEdit(); editor.Draft = "CREATE VIEW dbo.V AS SELECT 2 AS Id"; editor.Review();
            var save = editor.ConfirmSaveAsync();
            Check(!dialogs.TryCancelUnavailable(dialogs.Current!) && !pending.IsCompleted, "Save dialog dismissed early");
            completion.SetException(new InvalidOperationException("Save failed"));
            await save; Check(!await pending.WaitAsync(TimeSpan.FromSeconds(2)), "Unavailable dialog accepted");
            Check(editor.Error.Contains("Save failed") && editor.Draft.Contains("SELECT 2"), "Save failure or draft lost");
        });
    }
}
