namespace DataPieMaui;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(FileSystem.AppDataDirectory, "WebView2"));
        UserAppTheme = AppTheme.Light;
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = new MainPage();
        var window = new Window(page) { Title = "DataPieMaui", Width = 1024, Height = 760, MinimumWidth = 960, MinimumHeight = 650 };
        window.Created += (_, _) =>
        {
            var native = (Microsoft.UI.Xaml.Window)window.Handler!.PlatformView!;
            native.AppWindow.Closing += (_, args) =>
            {
                if (page.Workspace.CanClose && !page.HasPendingTableEdits) { page.Close(); return; }
                args.Cancel = true;
                if (page.HasPendingTableEdits)
                {
                    page.Workspace.Context.State.Status = "Save or cancel table editor drafts and wait for table or analysis operations to finish before closing the application.";
                    return;
                }
                page.Workspace.NotifyCloseBlocked();
            };
        };
        window.Destroying += (_, _) => page.Release();
        return window;
    }
}
