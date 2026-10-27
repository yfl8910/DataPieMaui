using DataPieCore;

namespace DataPieMaui.Services.Shared;

public sealed class AnalysisCalculationDraft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Column { get; set; } = "";
    public string Operation { get; set; } = "SUM";
    public string Name { get; set; } = "";
    public AnalysisNumberFormat? Format { get; set; }
    public AnalysisCalculation ToCalculation() => new(Column, Operation, Name, Id, Format);
}
public sealed class AnalysisFormulaDraft
{
    public string Id { get; set; } = "";
    public AnalysisNumberFormat? Format { get; set; }
    public string Name { get; set; } = "";
    public string Left { get; set; } = "";
    public string Operation { get; set; } = "/";
    public string Right { get; set; } = "";
    public bool UseConstant { get; set; }
    public decimal? Constant { get; set; }
    public AnalysisFormula ToFormula() => new(Name, Left, Operation, UseConstant ? "" : Right, UseConstant ? Constant : null, Id, Format);
}
public sealed record AnalysisCalculationSettings(IReadOnlyList<AnalysisCalculation> Calculations, bool IncludeCount, IReadOnlyList<AnalysisFormula> Formulas, string SortBy = "", bool SortDescending = false, AnalysisNumberFormat? CountFormat = null);
