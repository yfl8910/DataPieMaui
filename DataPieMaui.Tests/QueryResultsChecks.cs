using System.Data;
using DataPieCore;
using DataPieMaui.Components;
using DataPieMaui.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class QueryResultsChecks
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Numeric previews use two decimals without losing raw precision", () =>
        {
            foreach (var culture in new[] { "en-US", "de-DE" })
            {
                var previous = System.Globalization.CultureInfo.CurrentCulture;
                try
                {
                    var current = System.Globalization.CultureInfo.GetCultureInfo(culture);
                    System.Globalization.CultureInfo.CurrentCulture = current;
                    using var table = new DataTable();
                    table.Columns.Add("Value", typeof(object));
                    table.Rows.Add(12); table.Rows.Add(1.2345m); table.Rows.Add(-2.346d);
                    table.Rows.Add("001.2345"); table.Rows.Add(DBNull.Value); table.Rows.Add(true);
                    using var reader = table.CreateDataReader();
                    var raw = QueryPreviewResult.Read(reader, 1000);
                    var snapshot = new QueryResultSnapshot(raw, "Test");
                    Check(snapshot.FormatCell(0, 0) == "12", "Integer gained decimal places");
                    Check(snapshot.FormatCell(1, 0) == 1.2345m.ToString("F2", current) && snapshot.FormatCell(2, 0) == (-2.346d).ToString("F2", current), "Fractional rounding incorrect");
                    Check(snapshot.FormatCell(3, 0) == "001.2345" && snapshot.FormatCell(4, 0) == "NULL" && snapshot.FormatCell(5, 0) == "True", "Nonnumeric values changed");
                    Check((decimal)raw.GetValue(1, 0) == 1.2345m && snapshot.CopyText(new[] { 1 }).Contains(1.2345m.ToString(current)), "Copy or backing value lost precision");
                }
                finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
            }
            return Task.CompletedTask;
        });
        await test("Query results search spans preview pages and preserves filtering when sorting and copying", async () =>
        {
            using var data = new DataTable(); data.Columns.Add("Name", typeof(string));
            for (int i = 0; i < 205; i++) data.Rows.Add("row " + i);
            data.Rows.Add("MATCH %_'"); data.Rows.Add("match other"); data.Rows.Add(DBNull.Value);
            using var reader = data.CreateDataReader();
            var snapshot = new QueryResultSnapshot(QueryPreviewResult.Read(reader, 1000), "Test");
            var session = new QueryResultSession(); session.Set(snapshot);
            var desktop = new Desktop();
            using var component = new Harness { Preview = session, Desktop = desktop }; component.Initialize();
            component.Search(0, "match"); await component.CopyAsync(true);
            Check(desktop.Text == snapshot.CopyText(new[] { 205, 206 }), "Search missed later pages or ignored case incorrectly");
            component.Sort(0); await component.CopyAsync(true);
            Check(desktop.Text == snapshot.CopyText(snapshot.Sort(0, false).Where(i => i is 205 or 206)), "Sort lost search filter");
            component.Search(0, "%_'"); await component.CopyAsync(true);
            Check(desktop.Text == snapshot.CopyText(new[] { 205 }), "Search did not treat special characters literally");
            component.Search(0, "missing"); await component.CopyAsync(true);
            Check(desktop.Text == snapshot.CopyText(Array.Empty<int>()), "Empty search retained old rows");
            component.ClearSearch(); await component.CopyAsync(true);
            Check(desktop.Text == snapshot.CopyText(snapshot.Sort(0, false)), "Clear lost rows or sorting");
        });
        await test("Blazor results copy through desktop service and handle clipboard failure", async () =>
        {
            var session = new QueryResultSession();
            using var data = new DataTable(); data.Columns.Add("Name", typeof(string));
            data.Rows.Add("<script>unsafe</script>"); data.Rows.Add("second\tvalue");
            using var reader = data.CreateDataReader();
            var snapshot = new QueryResultSnapshot(QueryPreviewResult.Read(reader, 1000), "Test", "Table preview");
            session.Set(snapshot);
            var desktop = new Desktop();
            using var component = new Harness { Preview = session, Desktop = desktop };
            component.Initialize();
            component.BeginAdd(); component.BeginEdit(); component.RequestDelete();
            await component.SaveAsync(); await component.DeleteAsync();
            await component.CopyAsync(false);
            Check(desktop.Calls == 0, "No-selection copy reached clipboard");
            await component.CopyAsync(true);
            Check(desktop.Text == snapshot.CopyText(new[] { 0, 1 }), "Component did not delegate the formatted preview to desktop service");
            desktop.Fail = true;
            await component.CopyAsync(true);
            Check(desktop.Calls == 2, "Clipboard error path not exercised");
        });
        await test("Blazor result component renders safely and updates when session changes", async () =>
        {
            var services = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoopJs());
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var session = new QueryResultSession();
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<TableEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Preview"] = session, ["Desktop"] = new Desktop() }));
                Check(root.ToHtmlString().Contains("Run a query"), "Missing empty state");
                using var data = new DataTable(); data.Columns.Add("Name", typeof(string)); data.Rows.Add("<script>unsafe</script>");
                using var reader = data.CreateDataReader();
                session.Set(new QueryResultSnapshot(QueryPreviewResult.Read(reader, 1000), "Test", "Table preview"));
                var html = root.ToHtmlString();
                Check(!html.Contains("<h1>Table editor</h1>") && html.Contains("Read-only preview") && html.Contains("Table preview") && html.Contains("Copy all preview rows") && !html.Contains("<script>unsafe</script>"), "Unified preview missing or cell rendered as markup");
                Check(!html.Contains("Add record") && !html.Contains("Delete selected") && !html.Contains("data-dialog-resize"), "Read-only preview exposed write controls");
                session.Set(null);
                Check(root.ToHtmlString().Contains("Run a query"), "Cleared result remained visible");
            });
        });
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Harness : TableEditor
    {
        public void Initialize() => base.OnInitialized();
    }
    private sealed class NoopJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
    private sealed class Desktop : IDesktopServices
    {
        public string? Text;
        public bool Fail;
        public int Calls;
        public Task CopyTextAsync(string text)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("Clipboard unavailable");
            Text = text;
            return Task.CompletedTask;
        }
        public Task<string?> OpenAsync(params string[] extensions) => Task.FromResult<string?>(null);
        public Task<string?> SaveAsync(string name, string extension) => Task.FromResult<string?>(null);
        public Task<string?> SelectNewFilePathAsync(string name, string extension) => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync() => Task.FromResult<string?>(null);
        public Task ShowTablePreviewAsync(QueryResultSession session) => Task.CompletedTask;
        public Task ShowTableEditorAsync(DataPieCore.TableEditorSession session, string connectionLabel) => Task.CompletedTask;
        public Task ShowDataAnalysisAsync(DataPieMaui.Services.Shared.DataAnalysisSession session) => Task.CompletedTask;
    }
}
