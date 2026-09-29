using DataPieMaui.Services;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace DataPieMaui.Controls;

public sealed class QueryResultsPage : ContentPage
{
    private readonly BlazorWebView webView;

    public QueryResultsPage(QueryResultSession session)
    {
        Title = "Query results";
        webView = new BlazorWebView { HostPage = "wwwroot/index.html" };
        webView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(Components.QueryResults),
            Parameters = new Dictionary<string, object?> { ["Session"] = session }
        });
        Content = webView;
    }

    public void Release() => webView.Handler?.DisconnectHandler();
}
