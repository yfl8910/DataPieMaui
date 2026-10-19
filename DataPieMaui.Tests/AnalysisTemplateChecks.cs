using System.Data.SQLite;
using DataPieCore;
using DataPieMaui.Components;
using DataPieMaui.Services;
using DataPieMaui.Services.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class AnalysisTemplateChecks
{
    public static async Task Run(string root, Func<string, Func<Task>, Task> test)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        string database = Path.Combine(root, "template-source.db"); SQLiteConnection.CreateFile(database);
        string cs = new SQLiteConnectionStringBuilder { DataSource = database, FailIfMissing = true }.ToString();
        using (var db = new SQLiteConnection(cs))
        {
            db.Open(); using var create = new SQLiteCommand("CREATE TABLE Sales(Region TEXT, Amount NUMERIC, Units INTEGER); INSERT INTO Sales VALUES('North',100,4),('South',30,3)", db); create.ExecuteNonQuery();
        }
        var source = TableAnalysis.Open(cs, "SQLITE", "main", "Sales");
        string settingsPath = Path.Combine(root, "template-settings.db");
        var connections = new ConnectionStore(settingsPath);
        connections.Save(new Dbinfo { Dbname = "Existing connection", Dbtype = "SQLITE", ConnectionStrings = cs });
        var store = new AnalysisTemplateStore(settingsPath);
        var request = new AnalysisRequest(new[] { "Region" }, new[] { new AnalysisCalculation("Amount", "SUM", "Total revenue", "revenue"), new("Units", "SUM", "Total units", "units") }, true,
            "Region", "SecretFilterOnly", new[] { new AnalysisFormula("Average price", "revenue", "/", "units"), new("Half revenue", "revenue", "/", Constant: 2) });
        long first = 0, second = 0;
        await test("Analysis templates persist multiple named settings and overwrite the same table/name without changing saved connections", () =>
        {
            first = store.Save("Sales", " 区域均价 ", request);
            second = store.Save("Sales", "收入与销量", request with { Groups = Array.Empty<string>() });
            Check(first != second && store.List("Sales").Count == 2, "Different template names were overwritten");
            var original = store.Load("Sales", first);
            Check(original.Groups.SequenceEqual(request.Groups) && original.Calculations.SequenceEqual(request.Calculations) && original.Formulas!.SequenceEqual(request.Formulas!), "Template lost grouping order, names or formula identifiers");
            Check(original.SearchColumn == "" && original.SearchText == "" && original.EffectiveSearchColumns.Count == 0, "Search conditions were persisted");
            long andId = store.Save("Sales", "AND filters", request with { Conditions = new[] { new SearchCondition("Region", "Secret independent filter") } });
            Check(store.Load("Sales", andId).Conditions == null, "Independent search conditions were saved in a template");
            store.Delete("Sales", andId);
            var changed = request with { Formulas = new[] { request.Formulas![0], request.Formulas[1] with { Constant = 4 } } };
            Check(store.Save("Sales", "区域均价", changed) == first && store.List("Sales").Count == 2, "Same table/name did not overwrite atomically with a stable ID");
            var reopened = new AnalysisTemplateStore(settingsPath);
            Check(reopened.Load("Sales", first).Formulas![1].Constant == 4 && original.Formulas![1].Constant == 2, "Reopening lost updated data or mutated the old loaded settings");
            Check(connections.Load().Single().Dbname == "Existing connection" && connections.Load().Single().ConnectionStrings == cs, "Template storage modified connection records");
            using var db = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = settingsPath }.ToString()); db.Open();
            using var command = new SQLiteCommand("SELECT ConfigJson FROM AnalysisTemplates WHERE Id=@id", db); command.Parameters.AddWithValue("@id", first);
            string json = (string)command.ExecuteScalar();
            Check(!json.Contains("SecretFilterOnly") && !json.Contains("ConnectionString"), "Template stored search text or connection information");
            return Task.CompletedTask;
        });
        await test("Analysis templates filter by raw table name, validate targets and clear old results only after successful application", async () =>
        {
            long otherId = store.Save("OtherTable", "区域均价", request);
            Check(store.List("Sales").Count == 2 && store.List("OtherTable").Single().Id == otherId, "Template lists were not scoped to table name");
            try { store.Load("OtherTable", first); throw new Exception("Expected cross-table rejection"); } catch (InvalidOperationException) { }
            var session = new DataAnalysisSession(source, "Source", store);
            await session.RefreshTemplatesAsync();
            await session.AnalyzeAsync(request with { SearchText = "North" });
            Check(session.Result?.RowCount == 1, "Setup analysis failed");
            var applied = await session.LoadTemplateAsync(first);
            Check(applied != null && applied.SearchText == "" && session.Result == null && session.Totals == null && session.LastRequest == null, "Template application retained filters/results or ran automatically");
            await session.AnalyzeAsync(applied!);
            Check(session.Result?.RowCount == 2, "Applied settings did not calculate both regions");
            var previous = session.Result;
            long invalid = store.Save("Sales", "Missing fields", request with { Groups = new[] { "Missing region" }, Calculations = new[] { new AnalysisCalculation("Missing amount", "SUM") }, Formulas = null });
            Check(await session.LoadTemplateAsync(invalid) == null && ReferenceEquals(session.Result, previous) && session.Status.Contains("Missing region") && session.Status.Contains("Missing amount"), "Invalid template partially applied or failed to identify missing fields");
            Check(await session.DeleteTemplateAsync(invalid) && ReferenceEquals(session.Result, previous), "Deleting a template discarded analysis results");
            long? saved = await session.SaveTemplateAsync("Session save", request);
            Check(saved.HasValue && session.Templates.Any(t => t.Id == saved.Value), "Session save did not refresh available templates");
            Check(await session.SaveTemplateAsync("区域均价", request with { Groups = new[] { "Missing" } }) == null
                && store.Load("Sales", first).Groups.SequenceEqual(new[] { "Region" }), "Invalid settings overwrote the existing template");
        });
        await test("Analysis templates can be reused for compatible same-named tables in another database", async () =>
        {
            string otherPath = Path.Combine(root, "template-other-source.db"); SQLiteConnection.CreateFile(otherPath);
            string otherCs = new SQLiteConnectionStringBuilder { DataSource = otherPath, FailIfMissing = true }.ToString();
            using (var db = new SQLiteConnection(otherCs))
            {
                db.Open(); using var create = new SQLiteCommand("CREATE TABLE Sales(Region TEXT, Amount REAL, Units INTEGER); INSERT INTO Sales VALUES('Other',80,2)", db); create.ExecuteNonQuery();
            }
            var session = new DataAnalysisSession(TableAnalysis.Open(otherCs, "SQLITE", "main", "Sales"), "Other database", store);
            await session.RefreshTemplatesAsync();
            Check(session.Templates.Any(t => t.Id == first), "Same-named table did not see shared templates");
            var applied = await session.LoadTemplateAsync(first);
            Check(applied != null && session.Result == null, "Compatible template was rejected or ran immediately");
            await session.AnalyzeAsync(applied!);
            Check(session.Result?.RowCount == 1 && session.Result.FormatCell(0, 0) == "Other", "Template analysis used its old source database");
        });
        await test("Invalid template payloads are rejected and templates can be safely deleted without deleting source tables", () =>
        {
            long broken = store.Save("Sales", "Broken", request);
            using (var db = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = settingsPath }.ToString()))
            {
                db.Open();
                foreach (string json in new[] { "not json", "{\"Version\":99,\"Settings\":null}", "{\"Version\":1,\"Settings\":{\"Groups\":null}}" })
                {
                    using var update = new SQLiteCommand("UPDATE AnalysisTemplates SET ConfigJson=@json WHERE Id=@id", db);
                    update.Parameters.AddWithValue("@json", json); update.Parameters.AddWithValue("@id", broken); update.ExecuteNonQuery();
                    try { store.Load("Sales", broken); throw new Exception("Expected invalid payload rejection"); } catch (InvalidDataException) { }
                }
            }
            try { store.Delete("OtherTable", broken); throw new Exception("Expected wrong-table deletion rejection"); } catch (InvalidOperationException) { }
            store.Delete("Sales", broken);
            Check(!store.List("Sales").Any(t => t.Id == broken), "Deleted template remained");
            Check(source.Analyze(new(Array.Empty<string>(), Array.Empty<AnalysisCalculation>())).RowCount == 1, "Template deletion removed the source table");
            return Task.CompletedTask;
        });
        await test("Analysis template controls render with table-filtered names", async () =>
        {
            var session = new DataAnalysisSession(source, "Source", store); await session.RefreshTemplatesAsync();
            await using var provider = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoopJs()).BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var component = await renderer.RenderComponentAsync<DataAnalysis>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Session"] = session }));
                string html = component.ToHtmlString();
                Check(html.Contains("Apply template") && html.Contains("Save template") && html.Contains("Delete template") && html.Contains("Refresh templates") && html.Contains("Session save"), "Template controls or names were not rendered");
            });
        });
    }
    private sealed class NoopJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
