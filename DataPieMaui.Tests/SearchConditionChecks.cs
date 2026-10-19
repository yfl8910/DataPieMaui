using System.Data;
using System.Data.SQLite;
using DataPieCore;
using DataPieMaui.Components;
using DataPieMaui.Components.Shared;
using DataPieMaui.Services;
using Microsoft.AspNetCore.Components;

internal static class SearchConditionChecks
{
    public static async Task Run(string root, Func<string, Func<Task>, Task> test)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        string path = Path.Combine(root, "and-search.db"); SQLiteConnection.CreateFile(path);
        string cs = new SQLiteConnectionStringBuilder { DataSource = path, FailIfMissing = true }.ToString();
        using (var db = new SQLiteConnection(cs))
        {
            db.Open(); using var create = new SQLiteCommand("CREATE TABLE Orders(Id INTEGER PRIMARY KEY, Customer TEXT, Product TEXT, Amount NUMERIC, Flag BOOLEAN, SoldAt DATETIME); "
                + "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<1005) INSERT INTO Orders SELECT x,CASE WHEN x%2=0 THEN 'A' ELSE 'B' END,CASE WHEN x%3=0 THEN 'P' ELSE 'Q' END,x,x%2,'2026-10-19 08:00:00' FROM n; "
                + "INSERT INTO Orders VALUES(1006,'Z%_![''','P',999,0,'2026-10-19 08:00:00')", db); create.ExecuteNonQuery();
        }
        var conditions = new[] { new SearchCondition("Customer", "A"), new("Product", "P") };
        await test("Table editor export button saves filtered rows and releases busy state on save or cancel", async () =>
        {
            var session = TableEditorSession.Open(cs, "SQLITE", "", "Orders");
            try
            {
                session.Search(conditions);
                string output = Path.Combine(root, "editor-button.xlsx");
                var desktop = new Desktop { SavePath = output };
                using var editor = new TableEditor { Session = session, Desktop = desktop };
                await editor.ExportExcelAsync();
                Check(File.Exists(output) && desktop.SaveCalls == 1 && !session.IsBusy, "Export button did not save or release busy state");
                using var exported = MiniExcelLibs.MiniExcel.QueryAsDataTable(output, useHeaderRow: true);
                Check(exported.Rows.Count == 167, "Export button did not preserve active filters");
                desktop.SavePath = null;
                await editor.ExportExcelAsync();
                Check(desktop.SaveCalls == 2 && !session.IsBusy, "Canceling the save dialog left the editor busy");
            }
            finally { session.Data.Dispose(); }
        });
        await test("Independent AND filters drive analysis groups totals and complete exports", () =>
        {
            var source = TableAnalysis.Open(cs, "SQLITE", "main", "Orders");
            var request = new AnalysisRequest(new[] { "Customer", "Product" }, new[] { new AnalysisCalculation("Amount", "SUM") }, Conditions: conditions);
            var result = source.AnalyzeWithTotals(request);
            Check(result.Groups.RowCount == 1 && Convert.ToInt64(result.Totals.GetValue(0, 1)) == 167 && Convert.ToDouble(result.Totals.GetValue(0, 0)) == 84168, "Filters used OR or reused one keyword across fields");
            var none = source.AnalyzeWithTotals(request with { Conditions = new[] { new SearchCondition("Customer", "A"), new("Customer", "B") } });
            Check(none.Groups.RowCount == 0 && Convert.ToInt64(none.Totals.GetValue(0, 1)) == 0, "Repeated-field filters did not use AND");
            var literal = source.AnalyzeWithTotals(request with { Conditions = new[] { new SearchCondition("Customer", "%_!['"), new("Product", "P") } });
            Check(Convert.ToInt64(literal.Totals.GetValue(0, 1)) == 1, "Literal wildcard escaping failed");
            var injection = source.AnalyzeWithTotals(request with { Conditions = new[] { new SearchCondition("Customer", "A' OR 1=1 --") } });
            Check(injection.Groups.RowCount == 0, "Condition value was interpreted as SQL");
            string output = Path.Combine(root, "and-analysis.xlsx"); source.Export(request, output);
            using var exported = MiniExcelLibs.MiniExcel.QueryAsDataTable(output, useHeaderRow: true);
            Check(exported.Rows.Count == 1 && Convert.ToInt64(exported.Rows[0]["Records"]) == 167, "Export did not reuse AND filters");
            return Task.CompletedTask;
        });
        await test("Table editor AND search persists across paging and retains typed search and failed-search state", () =>
        {
            var session = TableEditorSession.Open(cs, "SQLITE", "", "Orders");
            session.Search(conditions);
            Check(session.TotalRows == 167 && session.Conditions.Count == 2 && session.HasNext, "Editor did not search the full table with both conditions");
            session.LoadPage(1);
            Check(session.Data.Rows.Count == 67 && session.Data.Rows.Cast<DataRow>().All(r => Convert.ToInt64(r["Id"]) % 6 == 0), "Paging lost an AND condition");
            string editorExport = Path.Combine(root, "editor-filtered.xlsx");
            session.ExportExcel(editorExport);
            using (var exported = MiniExcelLibs.MiniExcel.QueryAsDataTable(editorExport, useHeaderRow: true))
                Check(exported.Rows.Count == 167 && exported.Rows.Cast<DataRow>().All(r => Convert.ToInt64(r["Id"]) % 6 == 0), "Editor export lost filters or exported only the current page");
            Check(session.Page == 1 && session.Conditions.Count == 2, "Export changed the editor page or filters");
            session.Search(new[] { new SearchCondition("Customer", "A"), new("Id", "12"), new("SoldAt", "2026-10-19"), new("Flag", "false") });
            Check(session.TotalRows == 1 && Convert.ToInt64(session.Data.Rows[0]["Id"]) == 12, "Mixed numeric/date/boolean search failed");
            try { session.Search(new[] { new SearchCondition("Id", "not a number") }); throw new Exception("Expected invalid numeric condition rejection"); } catch (ArgumentException) { }
            Check(session.Conditions.Count == 4 && session.TotalRows == 1, "Failed search changed the active filters");
            session.Save(session.Data.Rows[0], new Dictionary<string, object> { ["Amount"] = 120m }); session.LoadPage(0);
            Check(session.TotalRows == 1 && session.Conditions.Count == 4, "Editing/refresh lost AND filters");
            session.Search(Array.Empty<SearchCondition>());
            Check(session.TotalRows == 1006 && session.Page == 0 && session.Conditions.Count == 0, "Clear search did not restore the full table");
            session.ExportExcel(editorExport);
            using (var exported = MiniExcelLibs.MiniExcel.QueryAsDataTable(editorExport, useHeaderRow: true))
                Check(exported.Rows.Count == 1006, "Unfiltered editor export did not include the whole table");
            session.Data.Dispose(); return Task.CompletedTask;
        });
        await test("Shared search predicate binds distinct SQL Server parameters and preserves type semantics", () =>
        {
            var types = new Dictionary<string, Type> { ["SoldAt"] = typeof(DateTime), ["Flag"] = typeof(bool), ["Amount"] = typeof(decimal), ["Customer"] = typeof(string) };
            var result = SearchConditions.BuildAnd(new[] { new SearchCondition("SoldAt", "2026-10-19"), new("Flag", "true"), new("Amount", "12.5"), new("Customer", "%_[") }, "SQLSERVER",
                n => new SearchColumn(n, types[n], SqlQueryBuilder.QuoteIdentifier(n, "SQLSERVER")));
            Check(result.Parameters.Count == 5 && result.Parameters.Select(p => p.Name).Distinct().Count() == 5 && result.Sql.Contains("AND"), "Parameter names collided across filters");
            Check(result.Parameters[0].Value is DateTime && result.Parameters[2].Value is true && result.Parameters[3].Value is decimal amount && amount == 12.5m
                && (string)result.Parameters[4].Value == "%!%!_![%", "Date/bool/numeric/text filter types were not preserved");
            return Task.CompletedTask;
        });
        await test("Shared search component updates immutable condition lists and readonly previews apply AND before copy", async () =>
        {
            IReadOnlyList<SearchCondition>? updated = null;
            IReadOnlyList<SearchCondition>? applied = null;
            bool cleared = false;
            var component = new ConditionSearch
            {
                Fields = new[] { new SearchField("Customer", "Customer"), new("Product", "Product") }, Value = conditions,
                ValueChanged = EventCallback.Factory.Create<IReadOnlyList<SearchCondition>>(new object(), value => updated = value),
                OnSearch = EventCallback.Factory.Create<IReadOnlyList<SearchCondition>>(new object(), value => applied = value),
                OnClear = EventCallback.Factory.Create(new object(), () => cleared = true)
            };
            await component.AddAsync(); Check(updated?.Count == 3 && conditions.Length == 2, "Adding a condition mutated the live input list");
            await component.SearchAsync(); Check(applied!.SequenceEqual(conditions), "Search did not publish independent conditions");
            await component.ClearAsync(); Check(cleared && updated?.Count == 0, "Clear did not publish empty filters");
            component.Value = Array.Empty<SearchCondition>();
            await component.AddAsync();
            Check(updated?.Count == 2 && updated.All(c => c.Column == "Customer" && c.Text == ""), "Initial blank condition was not preserved when adding a row");
            await component.ChangeAsync(0, new SearchCondition("Product", "P"));
            Check(updated?.Count == 1 && updated[0] == new SearchCondition("Product", "P"), "Initial blank condition could not be edited");
            updated = null; await component.RemoveAsync(0);
            Check(updated == null, "The last search row could be removed");
            component.Disabled = true; updated = null; await component.AddAsync(); Check(updated == null, "Disabled search accepted input");
            using var table = new DataTable(); table.Columns.Add("Customer"); table.Columns.Add("Product");
            table.Rows.Add("A", "P"); table.Rows.Add("A", "Q"); table.Rows.Add("B", "P"); table.Rows.Add("AP", "P");
            using var reader = table.CreateDataReader(); var snapshot = new QueryResultSnapshot(QueryPreviewResult.Read(reader, 1000), "Test");
            var session = new QueryResultSession(); session.Set(snapshot); var desktop = new Desktop();
            using var preview = new PreviewHarness { Preview = session, Desktop = desktop }; preview.Initialize();
            await preview.ApplyPreviewConditionsAsync(new[] { new SearchCondition("0", "A"), new("1", "P") }); await preview.CopyAsync(true);
            Check(desktop.Text == snapshot.CopyText(new[] { 0, 3 }), "Readonly preview did not AND independent fields");
            preview.Sort(0); await preview.CopyAsync(true); Check(desktop.Text == snapshot.CopyText(new[] { 0, 3 }), "Sorting lost preview filters");
            preview.ClearSearch(); await preview.CopyAsync(true); Check(desktop.Text == snapshot.CopyText(snapshot.Sort(0, false)), "Clearing preview filters lost rows or sort order");
        });
    }
    private sealed class PreviewHarness : TableEditor { public void Initialize() => base.OnInitialized(); }
    private sealed class Desktop : IDesktopServices
    {
        public string? Text;
        public string? SavePath;
        public int SaveCalls;
        public Task CopyTextAsync(string text) { Text = text; return Task.CompletedTask; }
        public Task<string?> OpenAsync(params string[] extensions) => Task.FromResult<string?>(null);
        public Task<string?> SaveAsync(string name, string extension) { SaveCalls++; return Task.FromResult(SavePath); }
        public Task<string?> SelectNewFilePathAsync(string name, string extension) => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync() => Task.FromResult<string?>(null);
        public Task ShowTablePreviewAsync(QueryResultSession session) => Task.CompletedTask;
        public Task ShowTableEditorAsync(TableEditorSession session, string connectionLabel) => Task.CompletedTask;
        public Task ShowDataAnalysisAsync(DataPieMaui.Services.Shared.DataAnalysisSession session) => Task.CompletedTask;
    }
}
