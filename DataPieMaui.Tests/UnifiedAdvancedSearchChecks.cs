using System.Data;
using System.Data.SQLite;
using DataPieCore;
using DataPieMaui.Components;
using DataPieMaui.Services;
using DataPieMaui.Services.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class UnifiedAdvancedSearchChecks
{
    public static async Task Run(string root, Func<string, Func<Task>, Task> test)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        AnalysisFilterRule Rule(string id, string column, string op, params string[] values) => new(id, column, op, values);
        string file = Path.Combine(root, "advanced-editor.db"); SQLiteConnection.CreateFile(file);
        string cs = new SQLiteConnectionStringBuilder { DataSource = file }.ToString();
        using (var db = new SQLiteConnection(cs))
        {
            db.Open(); using var cmd = new SQLiteCommand("CREATE TABLE Orders(Id INTEGER PRIMARY KEY, Customer TEXT, Qty INTEGER, SoldAt DATETIME); WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<205) INSERT INTO Orders SELECT x,CASE WHEN x%2=0 THEN 'A' ELSE 'B' END,x,'2026-01-31 12:00:00' FROM n", db); cmd.ExecuteNonQuery();
        }
        var config = new AnalysisFilterConfig(new[] { new AnalysisFilterGroup(new[] { Rule("a", "Customer", "Equals", "A"), Rule("n", "Qty", "Range", "1", "204") }),
            new AnalysisFilterGroup(new[] { Rule("b", "Customer", "Equals", "B"), Rule("m", "Qty", "Greater", "200") }) }, Array.Empty<AnalysisParameter>());
        await test("Table editor advanced OR search applies to all pages, refresh, edit and export", () =>
        {
            var session = TableEditorSession.Open(cs, "SQLITE", "", "Orders");
            try
            {
                session.SearchAdvanced(config);
                Check(session.TotalRows == 105 && session.Data.Rows.Count == 100 && session.HasNext, "Advanced search did not cover the full table");
                session.LoadPage(1); Check(session.Data.Rows.Count == 5 && session.AdvancedFilters != null, "Paging lost advanced filters");
                string path = Path.Combine(root, "advanced-editor.xlsx"); session.ExportExcel(path);
                using (var exported = MiniExcelLibs.MiniExcel.QueryAsDataTable(path, useHeaderRow: true)) Check(exported.Rows.Count == 105, "Export did not retain advanced search");
                try { session.SearchAdvanced(config with { Groups = new[] { new AnalysisFilterGroup(new[] { Rule("bad", "Qty", "Range", "bad", "10") }) } }); throw new Exception("Expected invalid number rejection"); }
                catch (ArgumentException) { }
                Check(session.Page == 1 && session.TotalRows == 105 && session.AdvancedFilters != null, "Failed search discarded existing state");
                session.LoadPage(0); session.Save(session.Data.Rows[0], new Dictionary<string, object> { ["Customer"] = "C" }); session.LoadPage(0);
                Check(session.TotalRows == 104 && session.AdvancedFilters != null, "Edit or refresh lost the filter");
                session.SearchAdvanced(null); Check(session.TotalRows == 205 && session.AdvancedFilters == null, "Clear search did not restore the full table");
            }
            finally { session.Data.Dispose(); }
            return Task.CompletedTask;
        });
        await test("Read-only preview advanced search uses typed values, OR and date ranges within the snapshot", async () =>
        {
            using var table = new DataTable(); table.Columns.Add("Customer", typeof(string)); table.Columns.Add("Amount", typeof(decimal)); table.Columns.Add("Date", typeof(DateTime));
            table.Rows.Add("A", 12m, new DateTime(2026, 1, 1)); table.Rows.Add("A", 21m, new DateTime(2026, 1, 31));
            table.Rows.Add("B", 99m, new DateTime(2026, 1, 31, 23, 59, 0)); table.Rows.Add("B", 99m, new DateTime(2026, 2, 1)); table.Rows.Add("C", DBNull.Value, new DateTime(2026, 1, 1));
            using var reader = table.CreateDataReader(); var snapshot = new QueryResultSnapshot(QueryPreviewResult.Read(reader, 1000), "Test");
            var previewSession = new QueryResultSession(); previewSession.Set(snapshot); var desktop = new Desktop();
            using var preview = new PreviewHarness { Preview = previewSession, Desktop = desktop }; preview.Initialize();
            var filter = new AnalysisFilterConfig(new[] { new AnalysisFilterGroup(new[] { Rule("a", "0", "Equals", "a"), Rule("n", "1", "Range", "10", "20") }),
                new AnalysisFilterGroup(new[] { Rule("b", "0", "Equals", "B"), Rule("d", "2", "Range", "2026-01-31", "2026-01-31") }) }, Array.Empty<AnalysisParameter>());
            preview.OpenAdvancedSearch(); await preview.ApplyAdvancedAsync(filter); await preview.CopyAsync(true);
            Check(desktop.Text == snapshot.CopyText(new[] { 0, 2 }), "Preview did not apply typed OR/date conditions");
            preview.OpenAdvancedSearch(); await preview.ApplyAdvancedAsync(filter with { Groups = new[] { new AnalysisFilterGroup(new[] { Rule("invalid", "1", "Range", "bad", "20") }) } });
            await preview.CopyAsync(true); Check(desktop.Text == snapshot.CopyText(new[] { 0, 2 }), "Invalid preview search changed active results");
            await preview.ApplyAdvancedAsync(filter);
            preview.ClearSearch(); await preview.CopyAsync(true); Check(desktop.Text == snapshot.CopyText(new[] { 0, 1, 2, 3, 4 }), "Single clear did not restore preview rows");
        });
        await test("Table editor renders a single clear action and the shared advanced search dialog", async () =>
        {
            var session = TableEditorSession.Open(cs, "SQLITE", "", "Orders");
            try
            {
                await using var provider = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoopJs()).BuildServiceProvider();
                await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
                await renderer.Dispatcher.InvokeAsync(async () =>
                {
                    EditorHarness? component = null;
                    var view = await renderer.RenderComponentAsync<EditorHarness>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Session"] = session, ["Desktop"] = new Desktop(), ["Capture"] = (Action<EditorHarness>)(c => component = c) }));
                    string html = view.ToHtmlString();
                    Check(html.Contains("Advanced search") && html.Split("Clear search").Length == 2 && !html.Contains("Clear filters"), "Editor does not display one clear-search action");
                    component!.OpenAdvancedSearch(); component.Render(); html = view.ToHtmlString();
                    Check(html.Contains("Qty") && html.Contains("Add OR group") && !html.Contains("Use parameter"), "Editor did not open the shared field-aware dialog");
                });
            }
            finally { session.Data.Dispose(); }
        });
        await test("Legacy search migrates without broadening optional OR groups", () =>
        {
            var advanced = new AnalysisFilterConfig(new[] { new AnalysisFilterGroup(new[] { Rule("a", "Customer", "Equals", "A") }),
                new AnalysisFilterGroup(new[] { Rule("b", "Customer", "Equals") with { ParameterId = "optional" } }) }, new[] { new AnalysisParameter("optional", "Customer", Array.Empty<string>()) });
            var source = TableAnalysis.Open(cs, "SQLITE", "", "Orders");
            var request = new AnalysisRequest(new[] { "Customer" }, Array.Empty<AnalysisCalculation>(), Conditions: new[] { new SearchCondition("Customer", "B") }, Filters: advanced);
            var before = source.Analyze(request);
            var merged = AnalysisFilters.MergeLegacy(advanced, request.Conditions, source.FilterFields);
            var after = source.Analyze(request with { Filters = merged, Conditions = null });
            Check(before.RowCount == 0 && after.RowCount == 0 && merged.CommonRules!.Count == 1, "Migrated search broadened an optional OR group");
            return Task.CompletedTask;
        });
        await test("Data analysis single clear removes both legacy and advanced conditions", async () =>
        {
            var source = TableAnalysis.Open(cs, "SQLITE", "", "Orders");
            var session = new DataAnalysisSession(source, "Test");
            var component = new DataAnalysis { Session = session };
            component.ApplyGroups(new[] { "Customer" }); component.ApplyFilters(config);
            component.SetConditions(new[] { new SearchCondition("Customer", "A") });
            await component.AnalyzeAsync();
            Check(session.LastRequest!.Filters != null && session.LastRequest.Conditions!.Count == 1, "Test did not activate both condition types");
            await component.ClearAllFiltersAsync();
            Check(session.LastRequest!.Filters == null && session.LastRequest.Conditions!.Count == 0 && session.Result!.RowCount == 3, "Clear search did not remove all filters or rerun analysis");
        });
    }
    private sealed class PreviewHarness : TableEditor { public void Initialize() => base.OnInitialized(); }
    public sealed class EditorHarness : TableEditor
    {
        [Parameter] public Action<EditorHarness>? Capture { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Capture?.Invoke(this); }
        public void Render() => StateHasChanged();
    }
    private sealed class NoopJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
    private sealed class Desktop : IDesktopServices
    {
        public string? Text;
        public Task CopyTextAsync(string text) { Text = text; return Task.CompletedTask; }
        public Task<string?> OpenAsync(params string[] extensions) => Task.FromResult<string?>(null);
        public Task<string?> SaveAsync(string name, string extension) => Task.FromResult<string?>(null);
        public Task<string?> SelectNewFilePathAsync(string name, string extension) => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync() => Task.FromResult<string?>(null);
        public Task ShowTablePreviewAsync(QueryResultSession session) => Task.CompletedTask;
        public Task ShowTableEditorAsync(TableEditorSession session, string connectionLabel) => Task.CompletedTask;
        public Task ShowDataAnalysisAsync(DataAnalysisSession session) => Task.CompletedTask;
    }
}
