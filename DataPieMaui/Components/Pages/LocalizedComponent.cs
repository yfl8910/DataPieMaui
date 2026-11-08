using Microsoft.AspNetCore.Components;

namespace DataPieMaui.Components.Pages;

public abstract class LocalizedComponent : ComponentBase
{
    [CascadingParameter(Name = "UiLanguage")] public string? Language { get; set; }

    protected virtual string CurrentLanguage => Language ?? "en";
    protected string L(string english, string chinese) => CurrentLanguage == "zh-CN" ? chinese : english;
}
