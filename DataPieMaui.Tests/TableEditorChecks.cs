using System.Data;
using System.Data.SQLite;
using DataPieCore;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

internal static class TableEditorChecks
{
    public static async Task Run(string root, Func<string, Func<Task>, Task> test)
    {
        string path = Path.Combine(root, "editor.db"); SQLiteConnection.CreateFile(path);
        string cs = new SQLiteConnectionStringBuilder { DataSource = path, FailIfMissing = true }.ToString();
        void Execute(string sql)
        {
            using var db = new SQLiteConnection(cs); db.Open(); using var cmd = new SQLiteCommand(sql, db); cmd.ExecuteNonQuery();
        }
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        void Conflict(Action action)
        {
            try { action(); throw new Exception("Expected conflict"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("No changes were committed"), "Unexpected failure: " + ex.Message); }
        }
        Execute("CREATE TABLE Items(Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Notes TEXT); WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<1005) INSERT INTO Items SELECT x,'name '||x,NULL FROM n; CREATE TABLE NoKey(Name TEXT); INSERT INTO NoKey VALUES('read only'); CREATE TABLE Composite(A INTEGER NOT NULL,B INTEGER NOT NULL,Value TEXT, PRIMARY KEY(A,B)); INSERT INTO Composite VALUES(1,1,'first'),(1,2,'second');");
        Execute("CREATE TABLE InsertRows(Id INTEGER PRIMARY KEY AUTOINCREMENT,Name TEXT NOT NULL UNIQUE,Amount INTEGER NOT NULL,Note TEXT DEFAULT 'default note',Optional TEXT,UpperName TEXT GENERATED ALWAYS AS (upper(Name)) STORED); CREATE TABLE DefaultOnly(Id INTEGER PRIMARY KEY); CREATE TABLE ManualKey(Id INTEGER PRIMARY KEY,Name TEXT) WITHOUT ROWID;");
        await test("Table editor searches across pages, preserves filters and treats wildcard input literally", () =>
        {
            Execute("CREATE TABLE SearchRows(Id INTEGER PRIMARY KEY,Name TEXT); WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<205) INSERT INTO SearchRows SELECT x,'match '||x FROM n; INSERT INTO SearchRows VALUES(206,'100%_![quoted]''');");
            var session = TableEditorSession.Open(cs, "SQLITE", "", "SearchRows");
            Check(session.TotalRows == 206, "Initial total is not the full table count");
            session.Search("Name", "match");
            Check(session.TotalRows == 205, "Search total does not count all matches");
            Check(session.Data.Rows.Count == 100 && session.HasNext, "Search was limited to visible rows");
            session.LoadPage(2);
            Check(session.TotalRows == 205, "Paging changed total count");
            Check(session.Data.Rows.Count == 5 && !session.HasNext && session.SearchColumn == "Name", "Paging lost filter");
            session.Search("Id", "205");
            Check(session.Page == 0 && session.Data.Rows.Count == 1 && Convert.ToInt64(session.Data.Rows[0]["Id"]) == 205, "Typed search failed");
            session.Save(session.Data.Rows[0], new Dictionary<string, object> { ["Name"] = "edited" });
            session.LoadPage(0);
            Check((string)session.Data.Rows[0]["Name"] == "edited", "Filtered edit failed");
            try { session.Search("Id", "invalid"); throw new Exception("Expected invalid input"); }
            catch (ArgumentException) { }
            Check(session.SearchColumn == "Id" && session.Data.Rows.Count == 1, "Failed search changed active results");
            session.Search("Name", "%_![quoted]'");
            Check(session.Data.Rows.Count == 1, "Wildcards or quotes were not treated literally");
            session.Delete(new[] { session.Data.Rows[0] }); session.LoadPage(0);
            Check(session.TotalRows == 0, "Delete did not refresh filtered count");
            Check(session.Data.Rows.Count == 0 && session.SearchColumn == "Name", "Filtered delete lost filter");
            session.Search("Name", "missing");
            Check(session.Data.Rows.Count == 0 && session.IsSearchable("Name"), "Empty results lost schema");
            session.Search(null, "");
            Check(session.TotalRows == 205, "Clear did not restore full table count");
            Check(session.SearchColumn == null && session.Data.Rows.Count == 100 && session.HasNext, "Clear did not restore table");
            session.Data.Dispose(); return Task.CompletedTask;
        });
        await test("Table editor inserts into empty tables using generated keys, defaults and computed columns", () =>
        {
            var session = TableEditorSession.Open(cs, "SQLITE", "", "InsertRows");
            Check(session.CanInsert && session.Data.Rows.Count == 0 && !session.IsInsertable("Id") && !session.IsInsertable("UpperName") && session.HasDefault("Note"), "Insert metadata failed on empty table");
            session.Insert(new Dictionary<string, object> { ["Name"] = "first", ["Amount"] = session.Parse("Amount", "7", false, true) });
            session.LoadPage(0);
            Check(Convert.ToInt64(session.Data.Rows[0]["Id"]) == 1 && Convert.ToInt64(session.Data.Rows[0]["Amount"]) == 7
                && (string)session.Data.Rows[0]["Note"] == "default note" && session.Data.Rows[0].IsNull("Optional") && (string)session.Data.Rows[0]["UpperName"] == "FIRST", "Insert did not preserve generated/default/NULL values");
            try { session.Insert(new Dictionary<string, object> { ["Name"] = "missing amount" }); throw new Exception("Expected required field error"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("Amount"), "Required field was not identified"); }
            try { session.Insert(new Dictionary<string, object> { ["Name"] = "first", ["Amount"] = 8 }); throw new Exception("Expected unique violation"); }
            catch (SQLiteException) { }
            session.LoadPage(0); Check(session.Data.Rows.Count == 1, "Failed insert was not rolled back"); session.Data.Dispose();
            var defaults = TableEditorSession.Open(cs, "SQLITE", "", "DefaultOnly");
            defaults.Insert(new Dictionary<string, object>()); defaults.LoadPage(0);
            Check(defaults.Data.Rows.Count == 1, "DEFAULT VALUES failed"); defaults.Data.Dispose();
            return Task.CompletedTask;
        });
        await test("Table editor inserts manual and composite primary keys without unlocking existing keys", () =>
        {
            var session = TableEditorSession.Open(cs, "SQLITE", "", "Composite");
            Check(session.IsInsertable("A") && session.IsInsertable("B") && !session.IsEditable("A"), "Insert and edit key permissions were conflated");
            session.Insert(new Dictionary<string, object> { ["A"] = session.Parse("A", "2", false, true), ["B"] = session.Parse("B", "1", false, true), ["Value"] = "inserted" });
            session.LoadPage(0); Check(session.Data.Rows.Count == 3, "Composite-key insertion failed"); session.Data.Dispose();
            var manual = TableEditorSession.Open(cs, "SQLITE", "", "ManualKey");
            Check(manual.IsInsertable("Id"), "WITHOUT ROWID key was incorrectly generated");
            manual.Insert(new Dictionary<string, object> { ["Id"] = manual.Parse("Id", "42", false, true) });
            manual.LoadPage(0); Check(Convert.ToInt64(manual.Data.Rows[0]["Id"]) == 42, "Manual key was not used"); manual.Data.Dispose();
            return Task.CompletedTask;
        });
        await test("Table editor pages beyond 1000 rows and saves typed values with stable keys", () =>
        {
            var session = TableEditorSession.Open(cs, "SQLITE", "", "Items");
            Check(session.CanEdit && session.Data.Rows.Count == 100 && session.HasNext && !session.IsEditable("Id"), "Initial page or key protection failed");
            session.LoadPage(10);
            Check(session.Data.Rows.Count == 5 && !session.HasNext && Convert.ToInt64(session.Data.Rows[0]["Id"]) == 1001, "Full table paging failed");
            var row = session.Data.Rows[0];
            session.Save(row, new Dictionary<string, object> { ["Name"] = session.Parse("Name", "x'); DROP TABLE Items;--", false), ["Notes"] = session.Parse("Notes", "", false) });
            session.LoadPage(10);
            Check((string)session.Data.Rows[0]["Name"] == "x'); DROP TABLE Items;--" && (string)session.Data.Rows[0]["Notes"] == "", "Values were not parameterized or empty text changed");
            session.Save(session.Data.Rows[0], new Dictionary<string, object> { ["Notes"] = session.Parse("Notes", "", true) });
            session.LoadPage(10); Check(session.Data.Rows[0].IsNull("Notes"), "NULL did not round trip");
            session.Data.Dispose();
            return Task.CompletedTask;
        });
        await test("Table editor rejects stale updates and rolls back multi-row deletes on conflicts", () =>
        {
            var session = TableEditorSession.Open(cs, "SQLITE", "", "Items");
            Execute("UPDATE Items SET Name='external' WHERE Id=2");
            Conflict(() => session.Save(session.Data.Rows[1], new Dictionary<string, object> { ["Name"] = "overwrite" }));
            Conflict(() => session.Delete(new[] { session.Data.Rows[0], session.Data.Rows[1] }));
            session.LoadPage(0);
            Check(Convert.ToInt64(session.Data.Rows[0]["Id"]) == 1 && (string)session.Data.Rows[1]["Name"] == "external", "Conflict did not roll back entire operation");
            session.Delete(new[] { session.Data.Rows[0] }); session.LoadPage(0);
            Check(Convert.ToInt64(session.Data.Rows[0]["Id"]) == 2, "Delete did not persist");
            session.Data.Dispose(); return Task.CompletedTask;
        });
        await test("Table editor supports composite keys and keyless SQLite records", () =>
        {
            var session = TableEditorSession.Open(cs, "SQLITE", "", "Composite");
            session.Save(session.Data.Rows[1], new Dictionary<string, object> { ["Value"] = "changed" }); session.LoadPage(0);
            Check((string)session.Data.Rows[0]["Value"] == "first" && (string)session.Data.Rows[1]["Value"] == "changed", "Composite key targeted wrong record");
            session.Data.Dispose();
            var readOnly = TableEditorSession.Open(cs, "SQLITE", "", "NoKey");
            Check(readOnly.CanEdit && readOnly.IsEditable("Name"), "Keyless table is still read-only");
            readOnly.Save(readOnly.Data.Rows[0], new Dictionary<string, object> { ["Name"] = "editable" });
            readOnly.LoadPage(0);
            Check((string)readOnly.Data.Rows[0]["Name"] == "editable" && readOnly.Data.Columns.Count == 1, "Keyless save failed or exposed rowid");
            readOnly.Data.Dispose(); return Task.CompletedTask;
        });
        await test("Keyless editors identify duplicate SQLite rows and reject ambiguous original-value writes atomically", () =>
        {
            Execute("CREATE TABLE Duplicates(Name TEXT, Payload BLOB); INSERT INTO Duplicates VALUES('same',X'01'),('same',X'01'); CREATE TABLE ShadowIds(rowid TEXT,_rowid_ TEXT,oid TEXT,Name TEXT); INSERT INTO ShadowIds VALUES('a','b','c','same'),('a','b','c','same'),('d','e','f','unique');");
            var rows = TableEditorSession.Open(cs, "SQLITE", "", "Duplicates");
            Check(rows.CanEdit && rows.IsEditable("Name") && !rows.IsEditable("Payload"), "Binary field blocked the whole table");
            rows.Save(rows.Data.Rows[0], new Dictionary<string, object> { ["Name"] = "changed" }); rows.LoadPage(0);
            Check(rows.Data.Rows.Count == 2 && (string)rows.Data.Rows[0]["Name"] == "changed" && (string)rows.Data.Rows[1]["Name"] == "same", "Duplicate row edit affected another row");
            Execute("UPDATE Duplicates SET Name='external' WHERE rowid=2");
            Conflict(() => rows.Delete(new[] { rows.Data.Rows[0], rows.Data.Rows[1] }));
            rows.LoadPage(0); Check(rows.Data.Rows.Count == 2, "Stale delete did not roll back the earlier row");
            rows.Delete(new[] { rows.Data.Rows[0] }); rows.LoadPage(0); Check(rows.Data.Rows.Count == 1, "Keyless deletion failed");
            rows.Data.Dispose();
            var fallback = TableEditorSession.Open(cs, "SQLITE", "", "ShadowIds");
            Check(fallback.CanEdit, "Original-value fallback disabled");
            Conflict(() => fallback.Save(fallback.Data.Rows[0], new Dictionary<string, object> { ["Name"] = "unsafe" }));
            Conflict(() => fallback.Delete(new[] { fallback.Data.Rows[2], fallback.Data.Rows[0] }));
            fallback.LoadPage(0); Check(fallback.Data.Rows.Count == 3 && (string)fallback.Data.Rows[0]["Name"] == "same", "Ambiguous update/delete was not rolled back");
            fallback.Save(fallback.Data.Rows[2], new Dictionary<string, object> { ["Name"] = "safe" });
            fallback.LoadPage(0); Check((string)fallback.Data.Rows[2]["Name"] == "safe", "Uniquely matched fallback row could not be edited");
            fallback.Data.Dispose(); return Task.CompletedTask;
        });
        await test("Table editor enforces foreign keys and quotes unusual table names", () =>
        {
            Execute("CREATE TABLE `Odd `` table.name`(Id INTEGER PRIMARY KEY, Value TEXT, Computed TEXT GENERATED ALWAYS AS (upper(Value)) STORED); INSERT INTO `Odd `` table.name`(Id,Value) VALUES(1,'ok'); CREATE TABLE Child(Id INTEGER REFERENCES `Odd `` table.name`(Id)); INSERT INTO Child VALUES(1);");
            var session = TableEditorSession.Open(cs, "SQLITE", "", "Odd ` table.name");
            Check(session.CanEdit, "Quoted table unavailable");
            Check(!session.IsEditable("Computed"), "Generated column must be read-only");
            try { session.Delete(new[] { session.Data.Rows[0] }); throw new Exception("Expected FK rejection"); } catch (SQLiteException) { }
            session.LoadPage(0); Check(session.Data.Rows.Count == 1, "FK violation deleted data");
            session.Data.Dispose(); return Task.CompletedTask;
        });
        await test("Table editor renders paging, write controls and read-only restrictions safely", async () =>
        {
            Execute("UPDATE Items SET Name='<script>unsafe</script>' WHERE Id=2");
            var session = TableEditorSession.Open(cs, "SQLITE", "", "Items");
            await using var provider = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new NoopJs()).BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var component = await renderer.RenderComponentAsync<DataPieMaui.Components.TableEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Session"] = session, ["ConnectionLabel"] = "Test database" }));
                string html = component.ToHtmlString();
                Check(!html.Contains("<h1>Table editor</h1>") && html.Contains("Delete selected") && html.Contains("Next") && html.Contains("Test database") && !html.Contains("<script>unsafe</script>"), "Missing controls or unescaped values");
                var keyless = TableEditorSession.Open(cs, "SQLITE", "", "NoKey");
                var readOnly = await renderer.RenderComponentAsync<DataPieMaui.Components.TableEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Session"] = keyless }));
                Check(readOnly.ToHtmlString().Contains("Table data") && readOnly.ToHtmlString().Contains("SQLite rowid"), "Keyless editing identity not explained");
                keyless.Data.Dispose();
            });
            session.Data.Dispose();
        });
        await test("Table editor dialogs retain failed drafts, confirm discard and delete only after confirmation", async () =>
        {
            Execute("CREATE TABLE DialogRows(Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Notes TEXT); INSERT INTO DialogRows VALUES(1,'original',NULL),(2,'second',NULL)");
            var session = TableEditorSession.Open(cs, "SQLITE", "", "DialogRows");
            await using var provider = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new NoopJs()).BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                EditorHarness? editor = null;
                var rootComponent = await renderer.RenderComponentAsync<EditorHarness>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Session"] = session, ["Capture"] = (Action<EditorHarness>)(component => editor = component)
                }));
                var original = session.Data.Rows[0];
                editor!.Select(original, true); editor.BeginEdit(); editor.Render();
                string html = rootComponent.ToHtmlString();
                Check(html.Contains("aria-modal=\"true\"") && html.Contains("Edit record") && html.Contains("readonly") && !html.Contains("Set to NULL"), "Missing edit modal, read-only fields or unexpected NULL checkbox");
                Check(html.Contains("data-movable=\"true\"") && html.Contains("data-dialog-maximize") && html.Contains("data-dialog-resize"), "Edit dialog missing window controls");
                Check(!session.HasDraft, "Opening an unchanged record marked it dirty");
                editor.SetDraft("Name", "draft text"); editor.RequestClose(); editor.Render();
                Check(rootComponent.ToHtmlString().Contains("Discard your unsaved changes?") && session.HasDraft, "Dirty close did not ask to discard");
                editor.DiscardDraft(); editor.Render();
                Check(!rootComponent.ToHtmlString().Contains("aria-modal") && !session.HasDraft, "Discard did not close dialog");
                editor.BeginEdit(); editor.SetDraft("Name", "retained draft");
                Execute("UPDATE DialogRows SET Name='external' WHERE Id=1");
                await editor.SaveAsync(); editor.Render();
                html = rootComponent.ToHtmlString();
                Check(html.Contains("retained draft") && html.Contains("No changes were committed") && session.HasDraft, "Failed save closed dialog or lost draft");
                session.IsBusy = true; editor.RequestClose(); editor.DiscardDraft();
                Check(session.HasDraft, "Busy dialog could be discarded"); session.IsBusy = false;
                editor.DiscardDraft(); editor.Select(original, false); session.LoadPage(0);
                editor.Select(session.Data.Rows[0], true); editor.BeginEdit(); editor.SetDraft("Name", "saved");
                await editor.SaveAsync(); editor.Render();
                Check(!rootComponent.ToHtmlString().Contains("aria-modal") && (string)session.Data.Rows[0]["Name"] == "saved", "Successful save did not close and refresh");
                Check(session.Data.Rows[0].IsNull("Notes"), "Saving another field replaced an untouched NULL");
                editor.Select(session.Data.Rows[0], true); editor.BeginEdit(); editor.SetDraft("Notes", "filled");
                await editor.SaveAsync();
                Check((string)session.Data.Rows[0]["Notes"] == "filled", "Original NULL field could not be edited");
                editor.Select(session.Data.Rows[0], true); editor.BeginEdit(); editor.SetDraft("Notes", "");
                await editor.SaveAsync();
                Check(!session.Data.Rows[0].IsNull("Notes") && (string)session.Data.Rows[0]["Notes"] == "", "Clearing text unexpectedly set NULL");
                editor.SelectPage(true); editor.RequestDelete(); editor.Render();
                Check(rootComponent.ToHtmlString().Contains("Delete 2 selected records?"), "Page selection did not select all loaded rows");
                editor.SelectPage(false); editor.Render();
                Check(rootComponent.ToHtmlString().Contains("Delete 2 selected records?"), "Modal allowed page selection to change");
                editor.RequestClose(); editor.SelectPage(false);
                editor.Select(session.Data.Rows[0], true); editor.RequestDelete(); editor.Render();
                Check(rootComponent.ToHtmlString().Contains("Selected record keys") && session.Data.Rows.Count == 2, "Delete lacks record summary or ran early");
                Check(!rootComponent.ToHtmlString().Contains("data-dialog-maximize") && !rootComponent.ToHtmlString().Contains("data-dialog-resize"), "Delete confirmation should stay compact");
                editor.RequestClose(); editor.Render();
                Check(!rootComponent.ToHtmlString().Contains("aria-modal") && session.Data.Rows.Count == 2, "Cancel deleted data");
                editor.RequestDelete(); await editor.DeleteAsync(); editor.Render();
                Check(!rootComponent.ToHtmlString().Contains("aria-modal") && session.Data.Rows.Count == 1, "Confirmed deletion failed");
            });
            session.Data.Dispose();
        });
        await test("Add record dialog preserves failed input, confirms discard and saves without NULL controls", async () =>
        {
            var session = TableEditorSession.Open(cs, "SQLITE", "", "InsertRows");
            await using var provider = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new NoopJs()).BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                EditorHarness? editor = null;
                var rendered = await renderer.RenderComponentAsync<EditorHarness>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Session"] = session, ["Capture"] = (Action<EditorHarness>)(component => editor = component)
                }));
                editor!.BeginAdd(); editor.Render();
                Check(rendered.ToHtmlString().Contains("New record") && !rendered.ToHtmlString().Contains("Set to NULL"), "Add dialog missing or NULL option reappeared");
                editor.SetDraft("Name", "new row"); editor.SetDraft("Amount", "invalid");
                await editor.SaveAsync(); editor.Render();
                Check(rendered.ToHtmlString().Contains("new row") && rendered.ToHtmlString().Contains("dialog-error") && session.HasDraft, "Failed insert lost input");
                editor.RequestClose(); editor.Render();
                Check(rendered.ToHtmlString().Contains("Discard your unsaved changes?"), "Insert draft could close without confirmation");
                editor.SetDraft("Amount", "9"); editor.SetDraft("Optional", "");
                await editor.SaveAsync(); editor.Render();
                Check(!rendered.ToHtmlString().Contains("aria-modal") && !session.HasDraft && session.Data.Rows.Count == 2, "Insert did not close and refresh");
                var inserted = session.Data.Rows.Cast<DataRow>().Single(r => (string)r["Name"] == "new row");
                Check((string)inserted["Optional"] == "" && (string)inserted["Note"] == "default note", "Empty text or untouched default changed");
            });
            session.Data.Dispose();
        });
    }
    private sealed class NoopJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
    public sealed class EditorHarness : DataPieMaui.Components.TableEditor
    {
        [Parameter] public Action<EditorHarness>? Capture { get; set; }
        protected override void OnInitialized() => Capture?.Invoke(this);
        public void Render() => StateHasChanged();
    }
}
