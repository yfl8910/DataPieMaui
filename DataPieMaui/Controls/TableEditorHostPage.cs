using DataPieCore;
using DataPieMaui.Services;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace DataPieMaui.Controls;

public sealed class TableEditorHostPage : ContentPage
{
    private readonly BlazorWebView webView;
    public TableEditorHostPage(TableEditorSession session, string connectionLabel, IDesktopServices desktop, string language)
        : this(new Dictionary<string, object?> { ["Session"] = session, ["ConnectionLabel"] = connectionLabel, ["Desktop"] = desktop }, language) { }

    public TableEditorHostPage(QueryResultSession preview, IDesktopServices desktop, string language)
        : this(new Dictionary<string, object?> { ["Preview"] = preview, ["Desktop"] = desktop }, language) { }

    private TableEditorHostPage(Dictionary<string, object?> parameters, string language)
    {
        Title = language == "zh-CN" ? "表编辑器" : "Table editor";
        parameters["InitialLanguage"] = language;
        webView = new BlazorWebView { HostPage = "wwwroot/table-editor.html" };
        webView.RootComponents.Add(new RootComponent
        {
            Selector = "#app", ComponentType = typeof(Components.TableEditor),
            Parameters = parameters
        });
        Content = webView;
    }
    public void Release() => webView.Handler?.DisconnectHandler();
}
