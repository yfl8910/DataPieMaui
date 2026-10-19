using DataPieCore;

namespace DataPieMaui.Services.Shared;

public sealed class AnalysisCalculationDraft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Column { get; set; } = "";
    public string Operation { get; set; } = "SUM";
    public string Name { get; set; } = "";
    public AnalysisCalculation ToCalculation() => new(Column, Operation, Name, Id);
}
public sealed class AnalysisFormulaDraft
{
    public string Name { get; set; } = "";
    public string Left { get; set; } = "";
    public string Operation { get; set; } = "/";
    public string Right { get; set; } = "";
    public bool UseConstant { get; set; }
    public decimal? Constant { get; set; }
    public AnalysisFormula ToFormula() => new(Name, Left, Operation, UseConstant ? "" : Right, UseConstant ? Constant : null);
}
public sealed record AnalysisCalculationSettings(IReadOnlyList<AnalysisCalculation> Calculations, bool IncludeCount, IReadOnlyList<AnalysisFormula> Formulas);
