using DataPieCore;
using DataPieMaui.Components.Shared;
using DataPieMaui.Services.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class CalculationSortInteractionChecks
{
    public static async Task Run(TableAnalysis source)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            AnalysisCalculationSettings? applied = null;
            var request = new AnalysisRequest(new[] { "Region" }, new[] { new AnalysisCalculation("Amount", "SUM", Id: "amount") });
            var dialog = new AnalysisCalculationDialog();
            int id = await renderer.Mount(dialog, new Dictionary<string, object?>
            {
                ["Source"] = source, ["Settings"] = request,
                ["OnApply"] = EventCallback.Factory.Create<AnalysisCalculationSettings>(new object(), value => applied = value)
            });
            await renderer.Select(id, "Sort by", "aggregate:amount");
            await renderer.Select(id, "Direction", "desc");
            Check(renderer.Value(id, "Sort by") == "aggregate:amount" && renderer.Value(id, "Direction") == "desc", "Sort selection reset after rendering");
            await dialog.ApplyAsync();
            Check(applied?.SortBy == "aggregate:amount" && applied.SortDescending, "Dropdown changes did not reach applied settings");
            await renderer.Select(id, "Sort by", "count");
            await renderer.Select(id, "Direction", "asc");
            await dialog.ApplyAsync();
            Check(applied?.SortBy == "count" && !applied.SortDescending, "Record count or ascending direction was not selectable");
            var restored = new AnalysisCalculationDialog();
            int reopened = await renderer.Mount(restored, new Dictionary<string, object?> { ["Source"] = source, ["Settings"] = request with { SortBy = "aggregate:amount", SortDescending = true } });
            Check(renderer.Value(reopened, "Sort by") == "aggregate:amount" && renderer.Value(reopened, "Direction") == "desc", "Reopened sort controls lost their saved values");
            await renderer.Select(id, "Sort by", ""); await dialog.ApplyAsync();
            Check(applied?.SortBy == "", "No default sort could not be selected");
        });
    }

    public static async Task RunReorderAndRightValue(TableAnalysis source, string root)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var renderer = new EventRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            AnalysisCalculationSettings? applied = null;
            var request = new AnalysisRequest(new[] { "Region" }, new[] { new AnalysisCalculation("Amount", "SUM", "Revenue", "amount"), new("Qty", "SUM", "Quantity", "units") },
                Formulas: new[] { new AnalysisFormula("Price", "amount", "/", "amount") }, SortBy: "aggregate:amount", SortDescending: true);
            var dialog = new AnalysisCalculationDialog();
            int id = await renderer.Mount(dialog, new Dictionary<string, object?>
            {
                ["Source"] = source, ["Settings"] = request,
                ["OnApply"] = EventCallback.Factory.Create<AnalysisCalculationSettings>(new object(), value => applied = value)
            });
            await renderer.Select(id, "Right aggregate Price", "units");
            dialog.MoveAggregateUp("amount"); // The first item must remain in place.
            await renderer.Fire(id, "aria-label", "Move up Quantity", "onclick", new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
            await dialog.ApplyAsync();
            Check(applied!.Calculations.Select(c => c.Key).SequenceEqual(new[] { "units", "amount" }) && applied.Formulas[0].Right == "units" && applied.SortBy == "aggregate:amount", "Move up changed identities, formula references or default sort");
            Check(request.Calculations.Select(c => c.Key).SequenceEqual(new[] { "amount", "units" }), "Moving up mutated the original settings before Apply");
            await renderer.Fire(id, "aria-label", "Move up Revenue", "onclick", new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
            await dialog.ApplyAsync();
            Check(applied!.Calculations[0].Key == "amount", "Repeated move-up did not exchange adjacent aggregates");
            await renderer.Fire(id, "aria-label", "Move up Quantity", "onclick", new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
            await dialog.ApplyAsync();
            var orderedRequest = request with { Calculations = applied.Calculations, Formulas = applied.Formulas };
            var result = source.Analyze(orderedRequest);
            Check(result.GetColumnName(1) == "Quantity" && result.GetColumnName(2) == "Revenue" && Math.Abs(Convert.ToDouble(result.GetValue(0, 4)) - 36d / 7) < 0.000001, "Reordered result columns or formula value was incorrect");
            var store = new AnalysisTemplateStore(Path.Combine(root, "reordered-template.db"));
            long template = store.Save("Sales", "Reordered", orderedRequest);
            Check(store.Load("Sales", template).Calculations.Select(c => c.Key).SequenceEqual(new[] { "units", "amount" }), "Template lost column order");
            string export = Path.Combine(root, "reordered-analysis.xlsx"); source.Export(orderedRequest, export);
            using (var data = MiniExcelLibs.MiniExcel.QueryAsDataTable(export, useHeaderRow: true))
                Check(data.Columns[1].ColumnName == "Quantity" && data.Columns[2].ColumnName == "Revenue", "Excel lost reordered columns");
            await renderer.Select(id, "Right value Price", "constant");
            await renderer.Select(id, "Constant Price", "2");
            await dialog.ApplyAsync();
            Check(applied!.Formulas[0].Constant == 2m && applied.Formulas[0].Right == "" && renderer.Value(id, "Right value Price") == "constant", "Constant mode failed or retained an active aggregate");
            await renderer.Select(id, "Right value Price", "aggregate");
            Check(renderer.Value(id, "Right aggregate Price") == "units", "Switching back lost the right aggregate");
            await dialog.ApplyAsync();
            Check(applied!.Formulas[0].Constant == null && applied.Formulas[0].Right == "units", "Aggregate mode retained an active constant");
            dialog.RemoveAggregate("units");
            applied = null; await dialog.ApplyAsync();
            Check(applied == null, "Removed formula reference was silently replaced");
            await renderer.Select(id, "Right aggregate Price", "amount"); await dialog.ApplyAsync();
            Check(applied!.Formulas[0].Right == "amount", "Replacement right aggregate could not be selected");
        });
    }

#pragma warning disable BL0006 // Exercise actual rendered event handlers, rather than calling change handlers directly.
    private sealed class EventRenderer(IServiceProvider services, ILoggerFactory logger) : Renderer(services, logger)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Component event failed", exception);
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        public async Task<int> Mount(IComponent component, Dictionary<string, object?> parameters)
        {
            int id = AssignRootComponentId(component); await RenderRootComponentAsync(id, ParameterView.FromDictionary(parameters)); return id;
        }
        private (ulong Handler, string Value) SelectInfo(int id, string label)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = frames.Array[i];
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName is not ("select" or "input")) continue;
                string? name = null, value = null; ulong handler = 0;
                for (int j = i + 1; j < frames.Count && frames.Array[j].FrameType == RenderTreeFrameType.Attribute; j++)
                {
                    var attribute = frames.Array[j];
                    if (attribute.AttributeName == "aria-label") name = attribute.AttributeValue?.ToString();
                    if (attribute.AttributeName == "value") value = attribute.AttributeValue?.ToString();
                    if (attribute.AttributeName == "onchange") handler = attribute.AttributeEventHandlerId;
                }
                if (name == label) return (handler, value ?? "");
            }
            throw new Exception("Missing select: " + label);
        }
        public string Value(int id, string label) => SelectInfo(id, label).Value;
        public Task Select(int id, string label, string value) => DispatchEventAsync(SelectInfo(id, label).Handler, null, new ChangeEventArgs { Value = value });
        public Task Fire(int id, string attributeName, string attributeValue, string eventName, EventArgs args)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames.Array[i].FrameType != RenderTreeFrameType.Element) continue;
                bool match = false; ulong handler = 0;
                for (int j = i + 1; j < frames.Count && frames.Array[j].FrameType == RenderTreeFrameType.Attribute; j++)
                {
                    var attribute = frames.Array[j];
                    if (attribute.AttributeName == attributeName && attribute.AttributeValue?.ToString() == attributeValue) match = true;
                    if (attribute.AttributeName == eventName) handler = attribute.AttributeEventHandlerId;
                }
                if (match && handler != 0) return DispatchEventAsync(handler, null, args);
            }
            throw new Exception("Missing event " + eventName + " for " + attributeValue);
        }
    }
#pragma warning restore BL0006
}
