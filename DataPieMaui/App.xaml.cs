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
        var window = new Window(page) { Title = "DataPieMaui", Width = 1280, Height = 850, MinimumWidth = 960, MinimumHeight = 650 };
        window.Created += (_, _) =>
        {
            var native = (Microsoft.UI.Xaml.Window)window.Handler!.PlatformView!;
            native.AppWindow.Closing += (_, args) =>
            {
                if (page.Workspace.CanClose) { page.Close(); return; }
                args.Cancel = true;
                page.Workspace.NotifyCloseBlocked();
            };
        };
        window.Destroying += (_, _) => page.Release();
        return window;
    }
}
