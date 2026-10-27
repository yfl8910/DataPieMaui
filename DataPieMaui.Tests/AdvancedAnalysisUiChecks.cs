using DataPieCore;
using DataPieMaui.Components;
using DataPieMaui.Components.Shared;
using DataPieMaui.Services.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class AdvancedAnalysisUiChecks
{
    public static async Task Run(TableAnalysis source, string root)
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var config = new AnalysisFilterConfig(new[] { new AnalysisFilterGroup(new[] { new AnalysisFilterRule("period-rule", "SoldAt", "Range", Array.Empty<string>(), "period") }),
            new AnalysisFilterGroup(new[] { new AnalysisFilterRule("customer-rule", "Customer", "Equals", new[] { "B" }) }) },
            new[] { new AnalysisParameter("period", "Period", new[] { "2026-01-01", "2026-01-31" }, true) });
        var request = new AnalysisRequest(new[] { "Customer" }, new[] { new AnalysisCalculation("Amount", "SUM") }, Filters: config);
        await using var services = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoopJs()).BuildServiceProvider();
        await using var html = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await html.Dispatcher.InvokeAsync(async () =>
        {
            FilterHarness? dialog = null; AnalysisFilterConfig? applied = null;
            var rendered = await html.RenderComponentAsync<FilterHarness>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Source"] = source, ["Settings"] = request, ["Capture"] = (Action<FilterHarness>)(c => dialog = c),
                ["OnApply"] = EventCallback.Factory.Create<AnalysisFilterConfig>(new object(), value => applied = value)
            }));
            string markup = rendered.ToHtmlString();
            Check(markup.Contains("OR condition group") && markup.Contains("Period") && markup.Contains("2026-01-31") && markup.Contains("type=\"date\""), "Filter dialog omitted OR groups, defaults or date inputs");
            await dialog!.ApplyAsync();
            Check(applied?.Groups.Count == 2 && applied.Parameters.Single().Required && !ReferenceEquals(applied, config), "Filter dialog did not publish an isolated valid configuration");
            var session = new DataAnalysisSession(source, "Test"); MainHarness? main = null;
            var mainView = await html.RenderComponentAsync<MainHarness>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Session"] = session, ["Capture"] = (Action<MainHarness>)(c => main = c) }));
            main!.ApplyGroups(request.Groups); main.ApplyFilters(config); main.Render();
            Check(mainView.ToHtmlString().Contains("Reset parameters") && mainView.ToHtmlString().Contains("Save current parameter values as defaults"), "Runtime parameter controls missing");
            Check(mainView.ToHtmlString().Split("Clear search").Length == 2 && !mainView.ToHtmlString().Contains("Clear filters"), "Data analysis did not use a single clear-search action");
            Check(mainView.ToHtmlString().Contains("Search conditions"), "Search field disappeared while advanced filters were active");
            main.SetParameterValues(new Dictionary<string, IReadOnlyList<string>> { ["period"] = new[] { "2026-02-01", "2026-02-01" } });
            main.ApplyFilters(config); // Inline Analyze reapplies edited filter definitions.
            main.SetConditions(new[] { new SearchCondition("Note", "x") });
            await main.AnalyzeAsync(); main.Render();
            Check(session.Result!.RowCount == 1 && session.Result.FormatCell(0, 0) == "B" && session.LastRequest!.Filters != null && session.LastRequest.Conditions!.Single().Text == "x" && session.LastRequest.ParameterValues!["period"][0] == "2026-02-01", "Quick search discarded advanced filters or runtime dates");
            main.SetConditions(Array.Empty<SearchCondition>()); await main.AnalyzeAsync(); main.Render();
            Check(session.LastRequest!.Filters != null && session.LastRequest.Conditions!.Count == 0 && mainView.ToHtmlString().Contains("Search conditions"), "Clearing the search removed advanced filters or hid search");
            var store = new AnalysisTemplateStore(Path.Combine(root, "visible-search-manager.db"));
            long templateId = store.Save(source.TemplateTableName, "Advanced and search", request with { Conditions = new[] { new SearchCondition("Note", "x") } });
            var managerSession = new DataAnalysisSession(source, "Test", store); await managerSession.RefreshTemplatesAsync();
            var manager = await html.RenderComponentAsync<AnalysisTemplateDialog>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Session"] = managerSession, ["SelectedId"] = templateId }));
            Check(manager.ToHtmlString().Contains("Existing search") && manager.ToHtmlString().Contains("Note") && manager.ToHtmlString().Contains("Period"), "Manager did not display both advanced filters and saved search");
            var managerEditor = new AnalysisTemplateDialog { Session = managerSession };
            await managerEditor.LoadAsync(templateId); managerEditor.ApplyFilters(AnalysisFilters.MergeLegacy(config, new[] { new SearchCondition("Note", "x") }, source.FilterFields)); await managerEditor.UpdateAsync();
            Check(store.Load(source.TemplateTableName, templateId).Filters.CommonRules!.Single().Values.Single() == "x", "Editing advanced filters removed the saved quick search");
        });
        using var renderer = new CalculationSortInteractionChecks.EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            IReadOnlyList<string>? edited = null;
            var input = new AnalysisFilterValues();
            int id = await renderer.Mount(input, new Dictionary<string, object?> { ["Kind"] = "Date", ["Operation"] = "Range", ["Label"] = "Period", ["Values"] = new[] { "2026-01-01", "2026-01-31" }, ["ValueChanged"] = EventCallback.Factory.Create<IReadOnlyList<string>>(new object(), values => edited = values) });
            await renderer.Select(id, "Period end", "2026-02-28");
            Check(edited?.SequenceEqual(new[] { "2026-01-01", "2026-02-28" }) == true, "Date input change did not preserve the other bound");
            var format = new AnalysisNumberFormatInput(); AnalysisNumberFormat? selected = null;
            int formatId = await renderer.Mount(format, new Dictionary<string, object?> { ["ValueChanged"] = EventCallback.Factory.Create<AnalysisNumberFormat?>(new object(), value => { selected = value; format.Value = value; }) });
            await renderer.Select(formatId, "Number format", "Percent");
            await renderer.Select(formatId, "Decimal places", "3");
            Check(selected is { Kind: "Percent", Decimals: 3 }, "Independent number format controls did not update");
        });
    }
    public sealed class FilterHarness : AnalysisFilterDialog
    {
        [Parameter] public Action<FilterHarness>? Capture { get; set; }
        protected override void OnInitialized() { base.OnInitialized(); Capture?.Invoke(this); }
    }
    public sealed class MainHarness : DataAnalysis
    {
        [Parameter] public Action<MainHarness>? Capture { get; set; }
        protected override void OnInitialized() => Capture?.Invoke(this);
        public void Render() => StateHasChanged();
    }
    private sealed class NoopJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
