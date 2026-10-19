using DataPieMaui.Services;
using DataPieMaui.Services.Shared;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace DataPieMaui.Controls;

public sealed class DataAnalysisHostPage : ContentPage
{
    private readonly BlazorWebView webView;
    public DataAnalysisHostPage(DataAnalysisSession session, IDesktopServices desktop)
    {
        Title = "Data analysis";
        webView = new BlazorWebView { HostPage = "wwwroot/data-analysis.html" };
        webView.RootComponents.Add(new RootComponent
        {
            Selector = "#app", ComponentType = typeof(Components.DataAnalysis),
            Parameters = new Dictionary<string, object?> { ["Session"] = session, ["Desktop"] = desktop }
        });
        Content = webView;
    }
    public void Release() => webView.Handler?.DisconnectHandler();
}
