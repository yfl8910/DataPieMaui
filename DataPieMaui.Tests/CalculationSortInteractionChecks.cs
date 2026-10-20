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
                if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "select") continue;
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
    }
#pragma warning restore BL0006
}
