using DataPieCore;
using System.Diagnostics;

namespace DataPieMaui.Services.Shared;

public sealed class DataAnalysisSession(TableAnalysis source, string connectionLabel, AnalysisTemplateStore? templateStore = null)
{
    public TableAnalysis Source { get; } = source;
    public string ConnectionLabel { get; } = connectionLabel;
    public bool Busy { get; private set; }
    public string Status { get; private set; } = "Choose grouping fields and calculations, then analyze.";
    public string ChineseStatus { get; private set; } = "选择分组字段和计算项，然后运行分析。";
    private void SetStatus(string english, string chinese) => (Status, ChineseStatus) = (english, chinese);
    private void SetError(Exception error)
    {
        Trace.TraceError("{0}", error);
        SetStatus(error.Message, "操作失败：" + error.Message);
    }
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
            SetStatus($"Loaded {Templates.Count} templates for {Source.TableName}.", $"已加载 {Source.TableName} 的 {Templates.Count} 个模板。");
        }
        catch (Exception ex) { SetError(ex); }
        finally { Busy = false; }
    }
    public async Task<long?> SaveTemplateAsync(string name, AnalysisRequest request, bool createOnly = false)
    {
        if (Busy || templateStore == null) return null;
        Busy = true;
        try
        {
            request = AnalysisTemplateStore.ForStorage(request);
            Source.ValidateRequest(request);
            long id = await Task.Run(() => templateStore.Save(Source.TemplateTableName, name, request, createOnly));
            Templates = await Task.Run(() => templateStore.List(Source.TemplateTableName));
            SetStatus(createOnly ? $"New template '{name.Trim()}' saved." : $"Template '{name.Trim()}' saved. An existing template with the same table and name is replaced.",
                createOnly ? $"已保存新模板「{name.Trim()}」。" : $"已保存模板「{name.Trim()}」，同一表下的同名模板已替换。");
            return id;
        }
        catch (Exception ex) { SetError(ex); return null; }
        finally { Busy = false; }
    }
    public async Task<bool> UpdateTemplateAsync(long id, string name, AnalysisRequest request)
    {
        if (Busy || templateStore == null) return false;
        Busy = true;
        try
        {
            request = AnalysisTemplateStore.ForStorage(request);
            Source.ValidateRequest(request);
            await Task.Run(() => templateStore.Update(Source.TemplateTableName, id, name, request));
            Templates = await Task.Run(() => templateStore.List(Source.TemplateTableName));
            SetStatus($"Template '{name.Trim()}' updated.", $"已更新模板「{name.Trim()}」。");
            return true;
        }
        catch (Exception ex) { SetError(ex); return false; }
        finally { Busy = false; }
    }
    public async Task<AnalysisRequest?> LoadTemplateAsync(long id, bool clearResults = true)
    {
        if (Busy || templateStore == null) return null;
        Busy = true;
        try
        {
            var request = await Task.Run(() => templateStore.Load(Source.TemplateTableName, id));
            var unavailable = request.Groups.Where(n => !Source.Fields.Any(f => f.Name == n && f.CanGroup))
                .Concat(request.Calculations.Where(c => !Source.NumericColumns.Contains(c.Column)).Select(c => c.Column)).Distinct().ToArray();
            if (unavailable.Length > 0) throw new InvalidOperationException("Template fields are missing or incompatible: " + string.Join(", ", unavailable));
            if (clearResults || !AnalysisFilters.NeedsReview(request.Filters)) Source.ValidateRequest(request);
            if (clearResults) { Result = null; Totals = null; LastRequest = null; }
            if (AnalysisFilters.NeedsReview(request.Filters))
                SetStatus("Legacy parameters need review. Enter fixed values or dates in Default filter conditions, then Update selected.",
                    "请检查旧版参数。在默认筛选条件中输入固定值或日期，然后点击“更新所选模板”。");
            else
                SetStatus("Template settings loaded.", "已加载模板设置。");
            return request;
        }
        catch (Exception ex) { SetError(ex); return null; }
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
            SetStatus("Template deleted. Current analysis settings are retained.", "已删除模板，当前分析设置已保留。");
            return true;
        }
        catch (Exception ex) { SetError(ex); return false; }
        finally { Busy = false; }
    }
    public bool Matches(AnalysisRequest request) => LastRequest is { } last && last.Groups.SequenceEqual(request.Groups)
        && last.Calculations.SequenceEqual(request.Calculations) && last.IncludeCount == request.IncludeCount
        && (last.SortBy ?? "") == (request.SortBy ?? "") && last.SortDescending == request.SortDescending
        && System.Text.Json.JsonSerializer.Serialize(last.Filters) == System.Text.Json.JsonSerializer.Serialize(request.Filters)
        && SameParameters(last, request)
        && last.CountFormat == request.CountFormat
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
    private static bool SameParameters(AnalysisRequest left, AnalysisRequest right)
    {
        if (left.Filters == null && right.Filters == null) return true;
        var a = AnalysisFilters.Resolve(left.Filters, left.ParameterValues); var b = AnalysisFilters.Resolve(right.Filters, right.ParameterValues);
        return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var values) && pair.Value.SequenceEqual(values));
    }
    public async Task AnalyzeAsync(AnalysisRequest request)
    {
        if (Busy) return;
        Busy = true;
        SetStatus("Analyzing all matching source rows…", "正在分析所有匹配的源数据…");
        var watch = Stopwatch.StartNew();
        try
        {
            request = request with { Groups = request.Groups.ToArray(), Calculations = request.Calculations.ToArray(), Formulas = request.Formulas?.ToArray(), SearchColumns = request.SearchColumns?.ToArray(), Conditions = request.Conditions?.ToArray() };
            request = request with { Filters = AnalysisFilters.Snapshot(request.Filters), ParameterValues = request.Filters == null ? null : AnalysisFilters.Resolve(request.Filters, request.ParameterValues) };
            var preview = await Task.Run(() => Source.AnalyzeWithTotals(request));
            var data = preview.Groups;
            Result = new QueryResultSnapshot(data, ConnectionLabel, Source.TableName + " · Analysis", TableAnalysis.ResultFormats(request));
            Totals = preview.Totals == null ? null : new QueryResultSnapshot(preview.Totals, ConnectionLabel, Source.TableName + " · Overall totals", TableAnalysis.ResultFormats(request, true));
            LastRequest = request;
            SetStatus($"Analysis completed in {watch.Elapsed.TotalSeconds:F1} s · {data.RowCount:N0} groups",
                $"分析完成，耗时 {watch.Elapsed.TotalSeconds:F1} 秒，共 {data.RowCount:N0} 个分组。");
        }
        catch (Exception ex) { SetError(ex); }
        finally { Busy = false; }
    }
    public async Task ExportAsync(IDesktopServices desktop, string templateName = "", bool formatted = true)
    {
        if (Busy || LastRequest == null) return;
        Busy = true;
        try
        {
            var request = LastRequest;
            var path = await desktop.SaveAsync(string.IsNullOrWhiteSpace(templateName) ? "Analysis" : templateName.Trim(), ".xlsx");
            if (path == null) { SetStatus("Export canceled.", "已取消导出。"); return; }
            SetStatus("Exporting all analysis groups…", "正在导出全部分析分组…");
            await Task.Run(() => Source.Export(request, path, formatted));
            SetStatus("Analysis exported · File: " + Path.GetFullPath(path), "分析结果已导出。文件：" + Path.GetFullPath(path));
        }
        catch (Exception ex) { SetError(ex); }
        finally { Busy = false; }
    }
    public void Reset()
    {
        if (Busy) return;
        Result = null; Totals = null; LastRequest = null;
        SetStatus("Choose grouping fields and calculations, then analyze.", "选择分组字段和计算项，然后运行分析。");
    }
}
