using DataPieCore;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace DataPieMaui.Controls;

public sealed class TableEditorHostPage : ContentPage
{
    private readonly BlazorWebView webView;
    public TableEditorHostPage(TableEditorSession session, string connectionLabel)
    {
        Title = "Table editor";
        webView = new BlazorWebView { HostPage = "wwwroot/table-editor.html" };
        webView.RootComponents.Add(new RootComponent
        {
            Selector = "#app", ComponentType = typeof(Components.TableEditor),
            Parameters = new Dictionary<string, object?> { ["Session"] = session, ["ConnectionLabel"] = connectionLabel }
        });
        Content = webView;
    }
    public void Release() => webView.Handler?.DisconnectHandler();
}
