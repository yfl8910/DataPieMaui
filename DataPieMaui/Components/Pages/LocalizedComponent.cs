using Microsoft.AspNetCore.Components;

namespace DataPieMaui.Components.Pages;

public abstract class LocalizedComponent : ComponentBase
{
    [CascadingParameter(Name = "UiLanguage")] public string? Language { get; set; }

    protected virtual string CurrentLanguage => Language ?? "en";
    protected string L(string english, string chinese) => CurrentLanguage == "zh-CN" ? chinese : english;
    protected string ObjectKind(string kind) => kind switch
    {
        "Table" => L("Table", "表"),
        "View" => L("View", "视图"),
        "Procedure" => L("Procedure", "存储过程"),
        _ => kind
    };

    protected string FilterOperation(string operation) => operation switch
    {
        "Equals" => L("Equals", "等于"),
        "NotEquals" => L("Not equal", "不等于"),
        "Contains" => L("Contains", "包含"),
        "Range" => L("Range (inclusive)", "范围（含边界）"),
        "In" => L("In multiple values", "属于多个值"),
        "IsNull" => L("Is NULL", "为 NULL"),
        "IsNotNull" => L("Is not NULL", "不为 NULL"),
        "Greater" => ">",
        "GreaterOrEqual" => ">=",
        "Less" => "<",
        "LessOrEqual" => "<=",
        _ => operation
    };
}
