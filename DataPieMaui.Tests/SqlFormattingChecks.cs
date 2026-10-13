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
        await test("SQL formatting separates statements and blocks without accumulating blank lines", () =>
        {
            var nl=Environment.NewLine;
            var formatted=SqlDisplayFormatter.Format("SELECT x FROM t; UPDATE t SET x=2; DELETE FROM t WHERE x=0; BEGIN SELECT x FROM t; END; SELECT y FROM t;");
            Check(formatted.Contains(";"+nl+nl+"UPDATE") && formatted.Contains(";"+nl+nl+"DELETE"), "Statement separators missing");
            Check(formatted.Contains("END ;"+nl+nl+"SELECT"), "Block separator missing");
            Check(!formatted.Contains(nl+nl+nl) && SqlDisplayFormatter.Format(formatted)==formatted,"Formatting accumulates empty lines");
            var implicitSql=SqlDisplayFormatter.Format("SELECT x FROM t\nUPDATE t SET x=1\nDELETE FROM t WHERE x=0");
            Check(implicitSql.Contains(nl+nl+"UPDATE") && implicitSql.Contains(nl+nl+"DELETE"),"Statements without semicolons are not separated");
            var insert=SqlDisplayFormatter.Format("INSERT INTO t(x) SELECT x FROM source UNION ALL SELECT x FROM other;");
            Check(!insert.Contains(nl+nl+"SELECT"),"INSERT SELECT or UNION was split as a new statement");
            var nested=SqlDisplayFormatter.Format("SELECT (SELECT x FROM t), CASE WHEN x=1 THEN 'a;b' ELSE 'c' END AS label FROM t;");
            Check(nested.Contains("'a;b'") && !nested.Contains("END"+nl+nl),"CASE or literal confused with statement end");
            return Task.CompletedTask;
        });
        await test("Formatting controls appear only when editing a definition draft", async () =>
        {
            var js = new ClipboardJs();
            var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(js);
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider,provider.GetRequiredService<ILoggerFactory>());
            const string original = "CREATE VIEW dbo.Report AS SELECT '<script>unsafe</script>' AS x;";
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<Harness>(ParameterView.FromDictionary(new Dictionary<string,object?> { ["Sql"] = original }));
                Check(!root.ToHtmlString().Contains("Format SQL") && !root.ToHtmlString().Contains("Show original"), "Viewing has formatting controls");
                await Harness.Current!.CopyAsync(); Check(js.Text == original, "Viewing did not copy original SQL");
                Check(!root.ToHtmlString().Contains("<script>unsafe</script>"), "Unsafe SQL rendering");
                var session = new DataPieMaui.Services.Shared.DefinitionEditorSession(new DBUtil.DefinitionObject("View","dbo","Report"), new DBUtil.ObjectDefinition(original) { CanEdit = true }, "test", (_,sql) => Task.FromResult(new DBUtil.ObjectDefinition(sql) { CanEdit = true }));
                session.BeginEdit();
                var edit = await renderer.RenderComponentAsync<DefinitionEditorView>(ParameterView.FromDictionary(new Dictionary<string,object?> { ["Session"] = session }));
                Check(edit.ToHtmlString().Contains("Format SQL"), "Editing has no formatting button");
                session.FormatDraft(); session.Review();
                var review = await renderer.RenderComponentAsync<DefinitionEditorView>(ParameterView.FromDictionary(new Dictionary<string,object?> { ["Session"] = session }));
                Check(!review.ToHtmlString().Contains("Format SQL"), "Save review has formatting controls");
            });
        });
    }
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    public sealed class Harness : SqlDefinitionView
    {
        public static Harness? Current;
        protected override void OnInitialized() => Current=this;

        public void Refresh() => StateHasChanged();
    }
    private sealed class ClipboardJs : IJSRuntime
    {
        public string? Text;
        public ValueTask<T> InvokeAsync<T>(string identifier,object?[]? args) { Text=(string?)args?[0]; return ValueTask.FromResult(default(T)!); }
        public ValueTask<T> InvokeAsync<T>(string identifier,CancellationToken token,object?[]? args) => InvokeAsync<T>(identifier,args);
    }
}
