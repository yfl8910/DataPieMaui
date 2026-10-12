using DataPieMaui.Services;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace DataPieMaui.Controls;

public sealed class QueryResultsHostPage : ContentPage
{
    private readonly BlazorWebView webView;

    public QueryResultsHostPage(QueryResultSession session, IDesktopServices desktop)
    {
        Title = "Query results";
        webView = new BlazorWebView { HostPage = "wwwroot/index.html" };
        webView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(Components.QueryResults),
            Parameters = new Dictionary<string, object?> { ["Session"] = session, ["Desktop"] = desktop }
        });
        Content = webView;
    }

    public void Release() => webView.Handler?.DisconnectHandler();
}
