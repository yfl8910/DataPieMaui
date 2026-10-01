using DataPieCore;
using DBUtil;
using DataPieMaui.Services.Shared;

internal static class DefinitionEditChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(DefinitionObject target, string sql)
    {
        try { DefinitionSqlValidator.PrepareAlter(target, sql); }
        catch (ArgumentException) { return; }
        throw new Exception("Unsafe or mismatched definition was accepted: " + sql);
    }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        var view = new DefinitionObject("View", "dbo", "Report", 1);
        var procedure = new DefinitionObject("Procedure", "dbo", "UpdateItem", 2);
        await test("Definition parser accepts only the selected module and preserves its body", () =>
        {
            const string body = " AS BEGIN SELECT 'CREATE VIEW dbo.Other AS SELECT 1', '-- GO'; UPDATE Items SET Name='value'; END";
            string sql = "-- CREATE is only a comment\nCREATE /* comment */ PROC [dbo].[UpdateItem]" + body;
            string prepared = DefinitionSqlValidator.PrepareAlter(procedure, sql);
            Check(prepared == sql.Replace("\nCREATE /*", "\nALTER /*"), "Body, comments or literals modified");
            Check(DefinitionSqlValidator.PrepareAlter(view, "CREATE OR ALTER VIEW [dbo].[Report] AS SELECT 1 AS Id").StartsWith("ALTER VIEW"), "CREATE OR ALTER not converted");
            var escaped = new DefinitionObject("View", "a.b", "a]b");
            Check(DefinitionSqlValidator.PrepareAlter(escaped, "CREATE VIEW [a.b].[a]]b] AS SELECT 1 AS Id").StartsWith("ALTER"), "Escaped identifier rejected");
            foreach (string invalid in new[] {
                "CREATE VIEW dbo.Other AS SELECT 1 AS Id", "CREATE VIEW other.Report AS SELECT 1 AS Id",
                "CREATE VIEW Report AS SELECT 1 AS Id", "CREATE VIEW dbo.report AS SELECT 1 AS Id",
                "CREATE VIEW dbo.Report AS SELECT 1 AS Id; DROP TABLE dbo.Items;",
                "CREATE VIEW dbo.Report AS SELECT 1 AS Id\nGO\nDELETE FROM Items;",
                "CREATE VIEW dbo.Report AS SELECT 1 AS Id\nGO", "SELECT 1", "CREATE VIEW dbo.Report AS SELECT",
                "CREATE VIEW dbo.Report WITH ENCRYPTION AS SELECT 1 AS Id",
                "CREATE PROCEDURE dbo.Report AS SELECT 1", "USE master; CREATE VIEW dbo.Report AS SELECT 1 AS Id" }) Reject(view, invalid);
            Reject(procedure, "CREATE PROCEDURE dbo.UpdateItem;2 AS SELECT 1");
            Reject(procedure, "CREATE PROCEDURE dbo.UpdateItem WITH ENCRYPTION AS SELECT 1");
            Reject(procedure, "CREATE PROCEDURE dbo.UpdateItem AS EXTERNAL NAME A.B.C");
            Reject(procedure, "CREATE PROCEDURE dbo.UpdateItem AS SELECT 1\nGO\nEXEC dbo.UpdateItem");
            return Task.CompletedTask;
        });
        await test("Definition conflict check includes text, version and persisted SET options", () =>
        {
            var original = new ObjectDefinition("CREATE VIEW dbo.Report AS SELECT 1 AS Id") { CanEdit = true, Version = "v1" };
            SqlServerDbAccess.EnsureUnchanged(original, original);
            foreach (var changed in new[] { original with { Sql = "other" }, original with { Version = "v2" }, original with { QuotedIdentifier = false }, original with { AnsiNulls = false } })
            {
                bool conflict = false;
                try { SqlServerDbAccess.EnsureUnchanged(original, changed); } catch (InvalidOperationException) { conflict = true; }
                Check(conflict, "Conflict was missed");
            }
            return Task.CompletedTask;
        });
        await test("Definition editor requires review, retains failed drafts and guards unsaved changes", async () =>
        {
            var original = new ObjectDefinition("CREATE VIEW dbo.Report AS SELECT 1 AS Id") { CanEdit = true, Version = "v1" };
            int calls = 0; bool fail = true;
            var editor = new DefinitionEditorSession(view, original, "test", (before, sql) =>
            {
                calls++;
                if (fail) throw new InvalidOperationException("Conflict or SQL error");
                return Task.FromResult(before with { Sql = sql, Version = "v2" });
            });
            editor.BeginEdit(); editor.Draft = "CREATE VIEW dbo.Report AS SELECT 2 AS Id";
            await editor.ConfirmSaveAsync(); Check(calls == 0, "Save bypassed review");
            editor.Review(); Check(editor.ReviewSql!.StartsWith("ALTER"), "Review missing normalized SQL");
            Check(editor.Difference().Any(l => l.Kind == "removed") && editor.Difference().Any(l => l.Kind == "added"), "Diff missing");
            await editor.ConfirmSaveAsync();
            Check(editor.Editing && editor.Draft.Contains("SELECT 2") && editor.Error.Contains("Conflict") && !editor.Saving, "Failed save lost draft");
            Check(!editor.RequestClose() && editor.DiscardAction == "close", "Dirty close bypassed prompt");
            editor.KeepEditing(); Check(editor.Dirty, "Keep editing lost draft");
            editor.CancelEditing(); Check(editor.DiscardAction == "edit", "Cancel did not confirm discard");
            editor.Discard(); Check(!editor.Editing && !editor.Dirty, "Discard failed");
            editor.BeginEdit(); editor.Draft = "CREATE VIEW dbo.Report AS SELECT 3 AS Id"; editor.Review();
            editor.Draft = "CREATE VIEW dbo.Report AS SELECT 4 AS Id";
            Check(editor.ReviewSql is null, "Changed draft retained stale confirmation");
            fail = false; editor.Review(); await editor.ConfirmSaveAsync();
            Check(!editor.Editing && !editor.Dirty && editor.Original.Sql.Contains("SELECT 4") && editor.Message.Contains("saved"), "Successful save not reloaded");
            var readOnly = new DefinitionEditorSession(view, original with { CanEdit = false }, "test", (_, _) => throw new Exception("Read-only save called"));
            readOnly.BeginEdit(); readOnly.Review(); await readOnly.ConfirmSaveAsync();
            Check(!readOnly.Editing, "Read-only definition editable");
        });
        await test("Definition editor blocks duplicate saves and close while saving", async () =>
        {
            var completion = new TaskCompletionSource<ObjectDefinition>();
            int calls = 0;
            var original = new ObjectDefinition("CREATE VIEW dbo.Report AS SELECT 1 AS Id") { CanEdit = true };
            var editor = new DefinitionEditorSession(view, original, "test", (_, _) => { calls++; return completion.Task; });
            editor.BeginEdit(); editor.Draft = "CREATE VIEW dbo.Report AS SELECT 2 AS Id"; editor.Review();
            var pending = editor.ConfirmSaveAsync(); await editor.ConfirmSaveAsync();
            Check(calls == 1 && editor.Saving && !editor.RequestClose(), "Save or close guard failed");
            completion.SetResult(original with { Sql = "ALTER VIEW dbo.Report AS SELECT 2 AS Id" }); await pending;
        });
    }
}
