using DataPieCore;
using DBUtil;

namespace DataPieMaui.Services.Shared;

// One dialog owns one draft. It is discarded when the dialog closes; no global SQL cache.
public sealed class DefinitionEditorSession(
    DefinitionObject target, ObjectDefinition original, string database,
    Func<ObjectDefinition, string, Task<ObjectDefinition>> save,
    Func<string, string>? prepare = null)
{
    public DefinitionObject Target { get; } = target;
    public string Database { get; } = database;
    public ObjectDefinition Original { get; private set; } = original;
    private string draft = original.Sql;
    public string Draft
    {
        get => draft;
        set { if (draft != value) { draft = value; ReviewSql = null; Error = ""; Message = ""; } }
    }
    public bool Editing { get; private set; }
    public bool Saving { get; private set; }
    public event Action? BecameIdle;
    public bool Dirty => Draft != Original.Sql;
    public string? ReviewSql { get; private set; }
    public string Error { get; private set; } = "";
    public string Message { get; private set; } = "";
    public string? DiscardAction { get; private set; }
    public void BeginEdit()
    {
        if (!Original.CanEdit || Saving) return;
        draft = Original.Sql; Editing = true; ReviewSql = null; Error = ""; Message = "";
    }
    public void Review()
    {
        if (!Editing || Saving || !Dirty) return;
        try { ReviewSql = prepare is null ? DefinitionSqlValidator.PrepareAlter(Target, Draft, Original.QuotedIdentifier) : prepare(Draft); Error = ""; }
        catch (Exception ex) { Error = ex.Message; ReviewSql = null; }
    }
    public void FormatDraft()
    {
        if (!Editing || Saving || !Original.CanEdit || ReviewSql != null || DiscardAction != null) return;
        try
        {
            var formatted = SqlDisplayFormatter.Format(Draft);
            Draft = formatted;
            Error = "";
            Message = "Draft formatted. Review and confirm save to update the definition.";
        }
        catch (Exception ex)
        {
            Error = "Formatting failed: " + ex.Message;
            Message = "";
        }
    }
    public void BackToEdit() { if (!Saving) ReviewSql = null; }
    public void CancelEditing()
    {
        if (Saving) return;
        if (Dirty) { DiscardAction = "edit"; return; }
        ResetEdit();
    }
    public bool RequestClose()
    {
        if (Saving) return false;
        if (Dirty) { DiscardAction = "close"; return false; }
        return true;
    }
    public void KeepEditing() => DiscardAction = null;
    public bool Discard()
    {
        if (Saving) return false;
        bool close = DiscardAction == "close";
        ResetEdit(); return close;
    }
    private void ResetEdit()
    {
        draft = Original.Sql; Editing = false; ReviewSql = null; Error = ""; DiscardAction = null;
    }
    public async Task ConfirmSaveAsync()
    {
        if (Saving || ReviewSql is not { } sql || !Original.CanEdit || DiscardAction != null) return;
        Saving = true; Error = "";
        try
        {
            var saved = await save(Original, sql);
            Original = saved; ResetEdit(); Message = "Definition saved and reloaded.";
        }
        catch (Exception ex) { Error = ex.Message; ReviewSql = null; }
        finally { Saving = false; BecameIdle?.Invoke(); }
    }

    public IEnumerable<(string Kind, string Text)> Difference()
    {
        var before = Original.Sql.Replace("\r\n", "\n").Split('\n');
        var after = (ReviewSql ?? Draft).Replace("\r\n", "\n").Split('\n');
        int prefix = 0, suffix = 0;
        while (prefix < Math.Min(before.Length, after.Length) && before[prefix] == after[prefix]) prefix++;
        while (suffix < Math.Min(before.Length, after.Length) - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        foreach (var line in before.Take(prefix).TakeLast(2)) yield return ("context", "  " + line);
        foreach (var line in before.Skip(prefix).Take(before.Length - prefix - suffix)) yield return ("removed", "- " + line);
        foreach (var line in after.Skip(prefix).Take(after.Length - prefix - suffix)) yield return ("added", "+ " + line);
        foreach (var line in after.Skip(after.Length - suffix).Take(2)) yield return ("context", "  " + line);
    }
}
