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
        var config = new AnalysisFilterConfig(new[] { new AnalysisFilterGroup(new[] { Rule("a", "Customer", "In", "A", "B"), Rule("n", "Qty", "Range", "1", "105") }),
            new AnalysisFilterGroup(new[] { Rule("m", "Qty", "Greater", "0") }) }, Array.Empty<AnalysisParameter>());
        await test("Table editor advanced AND search applies to all pages, refresh, edit and export", () =>
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
        await test("Read-only preview advanced search uses typed values, AND and date ranges within the snapshot", async () =>
        {
            using var table = new DataTable(); table.Columns.Add("Customer", typeof(string)); table.Columns.Add("Amount", typeof(decimal)); table.Columns.Add("Date", typeof(DateTime));
            table.Rows.Add("A", 12m, new DateTime(2026, 1, 1)); table.Rows.Add("A", 21m, new DateTime(2026, 1, 31));
            table.Rows.Add("B", 99m, new DateTime(2026, 1, 31, 23, 59, 0)); table.Rows.Add("B", 99m, new DateTime(2026, 2, 1)); table.Rows.Add("C", DBNull.Value, new DateTime(2026, 1, 1));
            using var reader = table.CreateDataReader(); var snapshot = new QueryResultSnapshot(QueryPreviewResult.Read(reader, 1000), "Test");
            var previewSession = new QueryResultSession(); previewSession.Set(snapshot); var desktop = new Desktop();
            using var preview = new PreviewHarness { Preview = previewSession, Desktop = desktop }; preview.Initialize();
            var filter = new AnalysisFilterConfig(new[] { new AnalysisFilterGroup(new[] { Rule("a", "0", "In", "a", "B"), Rule("n", "1", "Range", "10", "99") }),
                new AnalysisFilterGroup(new[] { Rule("d", "2", "Range", "2026-01-31", "2026-01-31") }) }, Array.Empty<AnalysisParameter>());
            await preview.ApplyAdvancedAsync(filter); await preview.CopyAsync(true);
            Check(desktop.Text == snapshot.CopyText(new[] { 1, 2 }), "Preview did not apply typed AND/date conditions");
            await preview.ApplyAdvancedAsync(filter with { Groups = new[] { new AnalysisFilterGroup(new[] { Rule("invalid", "1", "Range", "bad", "20") }) } });
            await preview.CopyAsync(true); Check(desktop.Text == snapshot.CopyText(new[] { 1, 2 }), "Invalid preview search changed active results");
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
                    Check(html.Contains("Search conditions") && html.Contains("aria-label=\"Search value\"") && html.Split("Clear search").Length == 2 && !html.Contains("Clear filters") && !html.Contains("modal-overlay"), "Editor does not display simple inline search and one clear action");
                    component!.OpenAdvancedSearch(); component.Render(); html = view.ToHtmlString();
                    Check(html.Contains("Qty") && html.Contains("All conditions must match (AND)") && !html.Contains("Add OR group") && !html.Contains("Add default parameter"), "Editor did not open the shared AND-only dialog");
                });
            }
            finally { session.Data.Dispose(); }
        });
        await test("Legacy search migrates without broadening optional conditions", () =>
        {
            var advanced = new AnalysisFilterConfig(new[] { new AnalysisFilterGroup(new[] { Rule("a", "Customer", "Equals", "A") }),
                new AnalysisFilterGroup(new[] { Rule("b", "Customer", "Equals") with { ParameterId = "optional" } }) }, new[] { new AnalysisParameter("optional", "Customer", Array.Empty<string>()) });
            var source = TableAnalysis.Open(cs, "SQLITE", "", "Orders");
            var request = new AnalysisRequest(new[] { "Customer" }, Array.Empty<AnalysisCalculation>(), Conditions: new[] { new SearchCondition("Customer", "B") }, Filters: advanced);
            var before = source.Analyze(request);
            var merged = AnalysisFilters.MergeLegacy(advanced, request.Conditions, source.FilterFields);
            var after = source.Analyze(request with { Filters = merged, Conditions = null });
            Check(before.RowCount == 0 && after.RowCount == 0 && merged.CommonRules!.Count == 1, "Migrated search broadened an optional condition");
            return Task.CompletedTask;
        });
        await test("Read-only search finds matches after row 1000 without rerunning the original query", async () =>
        {
            using (var db = new SQLiteConnection(cs))
            {
                db.Open(); using var cmd = new SQLiteCommand("CREATE TABLE LargeRows(Id INTEGER, Name TEXT); WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<1505) INSERT INTO LargeRows SELECT x,'row '||x FROM n", db); cmd.ExecuteNonQuery();
            }
            using var access = DBUtil.DbAccessFactory.Create(cs, "SQLITE");
            QueryPreviewResult? first = null;
            var (data, archive) = QueryPreview.LoadFull(access, "SELECT Id,Name,random() AS Nonce,X'00FEFF' AS Payload FROM LargeRows ORDER BY Id", 1000, result => first = result);
            string temporary = archive.StoragePath;
            var snapshot = new QueryResultSnapshot(data, "Test", archive: archive);
            var previewSession = new QueryResultSession(); previewSession.Set(snapshot); var desktop = new Desktop();
            var preview = new PreviewHarness { Preview = previewSession, Desktop = desktop }; preview.Initialize();
            try
            {
                Check(ReferenceEquals(first, data), "The completed archive did not reuse the early preview");
                Check(snapshot.TotalRows == 1505 && snapshot.RowCount == 1000 && snapshot.Truncated, "Initial query did not capture all rows with a bounded preview");
                string csvPath = Path.Combine(root, "archived-preview.csv");
                snapshot.Export(csvPath, csv: true);
                var lines = File.ReadAllLines(csvPath);
                Check(lines.Length == 1506 && lines[^1].StartsWith("1505,row 1505,") && lines[^1].EndsWith(",00FEFF"),
                    "Archive export lost rows beyond the preview");
                string excelPath = Path.Combine(root, "archived-preview.xlsx");
                snapshot.Export(excelPath, csv: false);
                Check(MiniExcelLibs.MiniExcel.Query(excelPath, useHeaderRow: true).Count() == 1505, "Excel archive export lost rows");
                var narrowMatch = snapshot.SearchFull(get => (string)get("1") == "row 1505", new[] { 1 });
                Check(narrowMatch.RowCount == 1 && ((byte[])narrowMatch.GetValue(0, 3)).SequenceEqual(new byte[] { 0, 254, 255 }),
                    "Column-limited search did not restore the binary value of the matching row");
                await preview.ApplyPreviewConditionsAsync(new[] { new SearchCondition("1", "row 1505") }); await preview.CopyAsync(true);
                Check(desktop.Text!.Contains("1505\trow 1505") && desktop.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2, "Simple search did not find rows beyond the initial preview");
                var config = new AnalysisFilterConfig(new[] { new AnalysisFilterGroup(new[] { Rule("last", "0", "Greater", "1500") }) }, Array.Empty<AnalysisParameter>());
                await preview.ApplyAdvancedAsync(config); await preview.CopyAsync(true);
                Check(desktop.Text!.Contains("1505\trow 1505") && desktop.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 6, "Search did not find matching rows beyond the initial preview");
                await preview.ApplyAdvancedAsync(config with { Groups = new[] { new AnalysisFilterGroup(new[] { Rule("first", "0", "Equals", "1") }) } });
                await preview.CopyAsync(true);
                Check(desktop.Text == snapshot.CopyText(new[] { 0 }), "Search reran the original query instead of using its snapshot");
                await preview.ApplyAdvancedAsync(config with { Groups = new[] { new AnalysisFilterGroup(new[] { Rule("many", "0", "Greater", "500") }) } });
                await preview.CopyAsync(true);
                Check(desktop.Text!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 1001 && desktop.Text.Contains("1500\trow 1500") && !desktop.Text.Contains("1501\trow 1501"), "Matching preview was not capped at 1000 after scanning the full result");
                preview.ClearSearch(); await preview.CopyAsync(true); Check(desktop.Text!.Contains("1000\trow 1000") && !desktop.Text.Contains("1001\trow 1001"), "Clear did not restore the initial preview");
            }
            finally { preview.Dispose(); previewSession.Set(null); }
            Check(!File.Exists(temporary), "Temporary query snapshot was not deleted");
            var (_, replacedArchive) = QueryPreview.LoadFull(access, "SELECT Id FROM LargeRows", 1000);
            string replacedPath = replacedArchive.StoragePath;
            var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var resumeSearch = new ManualResetEventSlim();
            var searching = Task.Run(() => replacedArchive.Search(_ =>
            {
                reading.TrySetResult();
                resumeSearch.Wait(TimeSpan.FromSeconds(10));
                return false;
            }));
            try
            {
                await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Run(replacedArchive.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
                Check(!searching.IsCompleted, "Disposal had to wait for the archive search");
            }
            finally { resumeSearch.Set(); replacedArchive.Dispose(); }
            try
            {
                await searching.WaitAsync(TimeSpan.FromSeconds(5));
                throw new Exception("Search continued after the result was replaced");
            }
            catch (OperationCanceledException) { }
            Check(!File.Exists(replacedPath), "Replaced archive was not deleted when the reader exited");

            var (wide, wideArchive) = QueryPreview.LoadFull(access, "SELECT randomblob(4096) AS Payload", 1000);
            using (wideArchive)
            {
                Check(wide.FormatGridCell(0, 0).Length == 257 && wide.FormatCell(0, 0).Length == 8192,
                    "Large binary grid preview affected full-value formatting");
            }
            using var cancellation = new CancellationTokenSource();
            try
            {
                var canceled = QueryPreview.LoadFull(access, "SELECT Id FROM LargeRows", 1000, _ => cancellation.Cancel(), cancellation.Token);
                canceled.Archive.Dispose();
                throw new Exception("Early preview was not published during capture or cancellation was ignored");
            }
            catch (OperationCanceledException) { }
            using var interrupt = new CancellationTokenSource();
            var blocked = Task.Run(() => QueryPreview.LoadFull(access,
                "WITH RECURSIVE n(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<1000000000) SELECT SUM(i) FROM n",
                1000, cancellation: interrupt.Token));
            await Task.Delay(150);
            Check(!blocked.IsCompleted, "The query should still be computing its first row");
            interrupt.Cancel();
            try
            {
                var completed = await blocked.WaitAsync(TimeSpan.FromSeconds(5));
                completed.Archive.Dispose();
                throw new Exception("The query completed instead of being interrupted");
            }
            catch (OperationCanceledException) { }
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
