using DataPieMaui.Services;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace DataPieMaui;

// The native page only hosts the web UI and owns desktop window integration.
public sealed class MainPage : ContentPage
{
    private readonly WindowsDesktopServices desktop;
    private readonly BlazorWebView? webView;
    public WorkspaceController Workspace { get; }

    public MainPage()
    {
        desktop = new WindowsDesktopServices(() => Window);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DataPie");
        Directory.CreateDirectory(directory);
        Workspace = new WorkspaceController(desktop, Path.Combine(directory, "connections.db"));
        try { _ = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch
        {
            // This message must work even when no web UI can be rendered.
            Content = new VerticalStackLayout
            {
                Padding = 32, Spacing = 16,
                Children =
                {
                    new Label { Text = "Microsoft Edge WebView2 Runtime is required", FontSize = 24 },
                    new Label { Text = "Install Microsoft Edge WebView2 Runtime, then restart DataPieMaui. The application uses it to display its interface." }
                }
            };
            return;
        }
        webView = new BlazorWebView { HostPage = "wwwroot/workspace.html" };
        webView.RootComponents.Add(new RootComponent
        {
            Selector = "#app", ComponentType = typeof(Components.Workspace),
            Parameters = new Dictionary<string, object?> { ["Controller"] = Workspace }
        });
        Content = webView;
    }

    public void Close()
    {
        Workspace.Dialogs.Complete(false);
        desktop.CloseResults();
    }
    public void Release() { Close(); webView?.Handler?.DisconnectHandler(); }
}
