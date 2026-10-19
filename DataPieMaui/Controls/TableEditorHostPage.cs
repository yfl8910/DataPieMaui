using DataPieCore;
using DataPieMaui.Services;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace DataPieMaui.Controls;

public sealed class TableEditorHostPage : ContentPage
{
    private readonly BlazorWebView webView;
    public TableEditorHostPage(TableEditorSession session, string connectionLabel, IDesktopServices desktop)
        : this(new Dictionary<string, object?> { ["Session"] = session, ["ConnectionLabel"] = connectionLabel, ["Desktop"] = desktop }) { }

    public TableEditorHostPage(QueryResultSession preview, IDesktopServices desktop)
        : this(new Dictionary<string, object?> { ["Preview"] = preview, ["Desktop"] = desktop }) { }

    private TableEditorHostPage(Dictionary<string, object?> parameters)
    {
        Title = "Table editor";
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
