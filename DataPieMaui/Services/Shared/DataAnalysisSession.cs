using DataPieCore;
using System.Diagnostics;

namespace DataPieMaui.Services.Shared;

public sealed class DataAnalysisSession(TableAnalysis source, string connectionLabel, AnalysisTemplateStore? templateStore = null)
{
    public TableAnalysis Source { get; } = source;
    public string ConnectionLabel { get; } = connectionLabel;
    public bool Busy { get; private set; }
    public string Status { get; private set; } = "Choose grouping fields and calculations, then analyze.";
    public QueryResultSnapshot? Result { get; private set; }
    public QueryResultSnapshot? Totals { get; private set; }
    public AnalysisRequest? LastRequest { get; private set; }
    public bool TemplatesEnabled => templateStore != null;
    public IReadOnlyList<AnalysisTemplateInfo> Templates { get; private set; } = [];
    public async Task RefreshTemplatesAsync()
    {
        if (Busy || templateStore == null) return;
        Busy = true;
        try
        {
            Templates = await Task.Run(() => templateStore.List(Source.TemplateTableName));
            Status = $"Loaded {Templates.Count} templates for {Source.TemplateTableName}.";
        }
        catch (Exception ex) { Status = ex.ToString(); }
        finally { Busy = false; }
    }
    public async Task<long?> SaveTemplateAsync(string name, AnalysisRequest request)
    {
        if (Busy || templateStore == null) return null;
        Busy = true;
        try
        {
            request = AnalysisTemplateStore.WithoutSearch(request);
            Source.ValidateRequest(request);
            long id = await Task.Run(() => templateStore.Save(Source.TemplateTableName, name, request));
            Templates = await Task.Run(() => templateStore.List(Source.TemplateTableName));
            Status = $"Template '{name.Trim()}' saved. An existing template with the same table and name is replaced.";
            return id;
        }
        catch (Exception ex) { Status = ex.ToString(); return null; }
        finally { Busy = false; }
    }
    public async Task<AnalysisRequest?> LoadTemplateAsync(long id)
    {
        if (Busy || templateStore == null) return null;
        Busy = true;
        try
        {
            var request = await Task.Run(() => templateStore.Load(Source.TemplateTableName, id));
            var unavailable = request.Groups.Where(n => !Source.Fields.Any(f => f.Name == n && f.CanGroup))
                .Concat(request.Calculations.Where(c => !Source.NumericColumns.Contains(c.Column)).Select(c => c.Column)).Distinct().ToArray();
            if (unavailable.Length > 0) throw new InvalidOperationException("Template fields are missing or incompatible: " + string.Join(", ", unavailable));
            Source.ValidateRequest(request);
            Result = null; Totals = null; LastRequest = null;
            Status = "Template applied. Search conditions cleared. Click Analyze to refresh results.";
            return request;
        }
        catch (Exception ex) { Status = ex.ToString(); return null; }
        finally { Busy = false; }
    }
    public async Task<bool> DeleteTemplateAsync(long id)
    {
        if (Busy || templateStore == null) return false;
        Busy = true;
        try
        {
            await Task.Run(() => templateStore.Delete(Source.TemplateTableName, id));
            Templates = await Task.Run(() => templateStore.List(Source.TemplateTableName));
            Status = "Template deleted. Current analysis settings are retained.";
            return true;
        }
        catch (Exception ex) { Status = ex.ToString(); return false; }
        finally { Busy = false; }
    }
    public bool Matches(AnalysisRequest request) => LastRequest is { } last && last.Groups.SequenceEqual(request.Groups)
        && last.Calculations.SequenceEqual(request.Calculations) && last.IncludeCount == request.IncludeCount
        && SameSearch(last, request)
        && (last.Formulas ?? []).SequenceEqual(request.Formulas ?? []);
    private static bool SameSearch(AnalysisRequest left, AnalysisRequest right)
    {
        if (left.Conditions != null || right.Conditions != null)
        {
            var a = left.Conditions ?? (string.IsNullOrEmpty(left.SearchText) ? Array.Empty<SearchCondition>() : null);
            var b = right.Conditions ?? (string.IsNullOrEmpty(right.SearchText) ? Array.Empty<SearchCondition>() : null);
            return a != null && b != null && a.Where(c => !string.IsNullOrEmpty(c.Text)).OrderBy(c => c.Column, StringComparer.Ordinal).ThenBy(c => c.Text, StringComparer.Ordinal)
                .SequenceEqual(b.Where(c => !string.IsNullOrEmpty(c.Text)).OrderBy(c => c.Column, StringComparer.Ordinal).ThenBy(c => c.Text, StringComparer.Ordinal));
        }
        return left.EffectiveSearchColumns.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(right.EffectiveSearchColumns.OrderBy(n => n, StringComparer.Ordinal)) && left.SearchText == right.SearchText;
    }
    public async Task AnalyzeAsync(AnalysisRequest request)
    {
        if (Busy) return;
        Busy = true;
        Status = "Analyzing all matching source rows…";
        var watch = Stopwatch.StartNew();
        try
        {
            request = request with { Groups = request.Groups.ToArray(), Calculations = request.Calculations.ToArray(), Formulas = request.Formulas?.ToArray(), SearchColumns = request.SearchColumns?.ToArray(), Conditions = request.Conditions?.ToArray() };
            var preview = await Task.Run(() => Source.AnalyzeWithTotals(request));
            var data = preview.Groups;
            Result = new QueryResultSnapshot(data, ConnectionLabel, Source.TableName + " · Analysis");
            Totals = preview.Totals == null ? null : new QueryResultSnapshot(preview.Totals, ConnectionLabel, Source.TableName + " · Overall totals");
            LastRequest = request;
            Status = $"Analysis completed in {watch.Elapsed.TotalSeconds:F1} s · {data.RowCount:N0} groups";
        }
        catch (Exception ex) { Status = ex.ToString(); }
        finally { Busy = false; }
    }
    public async Task ExportAsync(IDesktopServices desktop)
    {
        if (Busy || LastRequest == null) return;
        Busy = true;
        try
        {
            var request = LastRequest;
            var path = await desktop.SaveAsync("Analysis", ".xlsx");
            if (path == null) { Status = "Export canceled."; return; }
            Status = "Exporting all analysis groups…";
            await Task.Run(() => Source.Export(request, path));
            Status = "Analysis exported · File: " + Path.GetFullPath(path);
        }
        catch (Exception ex) { Status = ex.ToString(); }
        finally { Busy = false; }
    }
    public void Reset()
    {
        if (Busy) return;
        Result = null; Totals = null; LastRequest = null;
        Status = "Choose grouping fields and calculations, then analyze.";
    }
}
