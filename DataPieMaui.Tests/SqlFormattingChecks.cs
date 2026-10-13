using DataPieCore;
using DataPieMaui.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
internal static class SqlFormattingChecks
{
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("Draft formatting retains original, requires review and preserves failed input", async () =>
        {
            const string original = "CREATE VIEW [dbo].[Report] AS SELECT x,y FROM t WHERE x=1;";
            var saved = new TaskCompletionSource<DBUtil.ObjectDefinition>(); int saves = 0;
            var session = new DataPieMaui.Services.Shared.DefinitionEditorSession(new DBUtil.DefinitionObject("View", "dbo", "Report"), new DBUtil.ObjectDefinition(original) { CanEdit = true }, "test", (_, sql) => { saves++; return saved.Task; });
            session.FormatDraft(); Check(session.Draft == original, "Viewing mode modified draft");
            session.BeginEdit(); session.FormatDraft();
            var formatted = session.Draft;
            Check(session.Dirty && session.Original.Sql == original && saves == 0 && formatted == SqlDisplayFormatter.Format(original), "Formatting saved or altered original");
            Check(!session.RequestClose(), "Formatted draft bypassed unsaved-change guard");
            session.KeepEditing();
            session.Draft = "CREATE VIEW [dbo].[Report] AS SELECT 'unclosed";
            var invalid = session.Draft; session.FormatDraft();
            Check(session.Draft == invalid && session.Error.Contains("Formatting failed") && saves == 0, "Failed formatting discarded edits");
            session.Draft = formatted; session.Review();
            Check(session.ReviewSql != null, "Formatted draft failed review");
            var reviewed = session.ReviewSql; session.FormatDraft(); Check(session.ReviewSql == reviewed, "Formatting changed save review");
            var pending = session.ConfirmSaveAsync();
            Check(session.Saving && saves == 1, "Save did not await confirmation");
            session.FormatDraft(); Check(session.Draft == formatted && session.Saving, "Formatting ran during save");
            saved.SetResult(new DBUtil.ObjectDefinition(reviewed!) { CanEdit = true }); await pending;
            Check(!session.Dirty && !session.Editing, "Save did not finish normally");
        });
        await test("SQL display formatting preserves literals, comments and batch separators", () =>
        {
            var sql = "CREATE PROCEDURE [dbo].[Odd]]Name] AS BEGIN SELECT N'a,b;-- WHERE', 'it''s', [FROM], \"WHERE\" FROM [dbo].[T] /* outer /* nested */ comment */ WHERE x=1; -- keep comment\nEND\nGO 2\nSELECT X'00ff';";
            var formatted = SqlDisplayFormatter.Format(sql);
            foreach (var text in new[] { "N'a,b;-- WHERE'", "'it''s'", "[Odd]]Name]", "[FROM]", "\"WHERE\"", "/* outer /* nested */ comment */", "-- keep comment", "GO 2", "X'00ff'" })
                Check(formatted.Contains(text), "Protected SQL changed: " + text);
            Check(formatted.Contains(Environment.NewLine+"GO 2"+Environment.NewLine), "GO is not on its own line");
            Check(SqlDisplayFormatter.Format(formatted)==formatted, "Formatter is not stable");
            var sqlite = "CREATE VIEW `Odd``Name` AS SELECT 'a,b', x FROM t WHERE x > 0;";
            Check(SqlDisplayFormatter.Format(sqlite).Contains("`Odd``Name`"), "SQLite quoting changed");
            try { SqlDisplayFormatter.Format("SELECT 'unclosed"); throw new Exception("Expected formatting failure"); }
            catch (InvalidOperationException) { }
            return Task.CompletedTask;
        });
        await test("Definition formatting is display-only, copies display and restores original", async () =>
        {
            var js = new ClipboardJs();
            var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(js);
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider,provider.GetRequiredService<ILoggerFactory>());
            const string original = "SELECT '<script>unsafe</script>', x FROM t WHERE x=1;";
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<Harness>(ParameterView.FromDictionary(new Dictionary<string,object?> { ["Sql"] = original }));
                var component = Harness.Current!;
                Check(root.ToHtmlString().Contains("Format SQL") && root.ToHtmlString().Contains("Show original"), "Missing display controls");
                await component.FormatRefresh();
                Check(component.Sql==original && !root.ToHtmlString().Contains("<script>unsafe</script>"), "Original SQL changed or unsafe rendering");
                await component.CopyAsync();
                Check(js.Text==SqlDisplayFormatter.Format(original), "Copy did not use formatted display");
                component.ShowOriginal(); component.Refresh(); await component.CopyAsync();
                Check(js.Text==original, "Show original did not restore copy content");
            });
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<Harness>(ParameterView.FromDictionary(new Dictionary<string,object?> { ["Sql"] = "SELECT 'unclosed" }));
                await Harness.Current!.FormatRefresh();
                Check(root.ToHtmlString().Contains("Formatting failed"), "Failure message missing");
                await Harness.Current.CopyAsync(); Check(js.Text=="SELECT 'unclosed", "Failed format changed display");
                var review = await renderer.RenderComponentAsync<SqlDefinitionView>(ParameterView.FromDictionary(new Dictionary<string,object?> { ["Sql"] = original, ["AllowFormatting"] = false }));
                Check(!review.ToHtmlString().Contains("Format SQL"), "Save review can be formatted");
            });
        });
    }
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    public sealed class Harness : SqlDefinitionView
    {
        public static Harness? Current;
        protected override void OnInitialized() => Current=this;
        public async Task FormatRefresh() { await FormatAsync(); StateHasChanged(); }
        public void Refresh() => StateHasChanged();
    }
    private sealed class ClipboardJs : IJSRuntime
    {
        public string? Text;
        public ValueTask<T> InvokeAsync<T>(string identifier,object?[]? args) { Text=(string?)args?[0]; return ValueTask.FromResult(default(T)!); }
        public ValueTask<T> InvokeAsync<T>(string identifier,CancellationToken token,object?[]? args) => InvokeAsync<T>(identifier,args);
    }
}
