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
        await test("Blazor results copy through desktop service and handle clipboard failure", async () =>
        {
            var session = new QueryResultSession();
            using var data = new DataTable(); data.Columns.Add("Name", typeof(string));
            data.Rows.Add("<script>unsafe</script>"); data.Rows.Add("second\tvalue");
            using var reader = data.CreateDataReader();
            var snapshot = new QueryResultSnapshot(QueryPreviewResult.Read(reader, 1000), "Test", "Table preview");
            session.Set(snapshot);
            var desktop = new Desktop();
            using var component = new Harness { Session = session, Desktop = desktop };
            component.Initialize();
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
            var services = new ServiceCollection().AddLogging();
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var session = new QueryResultSession();
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<QueryResults>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Session"] = session, ["Desktop"] = new Desktop() }));
                Check(root.ToHtmlString().Contains("Run a query"), "Missing empty state");
                using var data = new DataTable(); data.Columns.Add("Name", typeof(string)); data.Rows.Add("<script>unsafe</script>");
                using var reader = data.CreateDataReader();
                session.Set(new QueryResultSnapshot(QueryPreviewResult.Read(reader, 1000), "Test", "Table preview"));
                var html = root.ToHtmlString();
                Check(html.Contains("Table preview") && html.Contains("Copy all preview rows") && !html.Contains("<script>unsafe</script>"), "Result update missing or cell rendered as markup");
                session.Set(null);
                Check(root.ToHtmlString().Contains("Run a query"), "Cleared result remained visible");
            });
        });
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Harness : QueryResults
    {
        public void Initialize() => base.OnInitialized();
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
        public Task<string?> FolderAsync() => Task.FromResult<string?>(null);
        public Task ShowResultsAsync(QueryResultSession session) => Task.CompletedTask;
    }
}
