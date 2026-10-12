namespace DataPieMaui.Services;

internal sealed class WindowsDesktopServices(Func<Window?> owner) : IDesktopServices
{
    private Window? resultWindow;
    private Window Owner => owner() ?? throw new InvalidOperationException("The application window is not ready.");
    public Task<string?> OpenAsync(params string[] extensions) => MainThread.InvokeOnMainThreadAsync(() => WindowsFiles.OpenAsync(Owner, extensions));
    public Task<string?> SaveAsync(string name, string extension) => MainThread.InvokeOnMainThreadAsync(() => WindowsFiles.SaveAsync(Owner, name, extension));
    public Task<string?> FolderAsync() => MainThread.InvokeOnMainThreadAsync(() => WindowsFiles.FolderAsync(Owner));
    public Task CopyTextAsync(string text) => MainThread.InvokeOnMainThreadAsync(() => Clipboard.Default.SetTextAsync(text));
    public Task ShowResultsAsync(QueryResultSession session) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        if (resultWindow is { } existing)
        {
            if (existing.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
            {
                if (native.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter) presenter.Restore();
                native.Activate();
            }
            return;
        }
        var page = new Controls.QueryResultsHostPage(session, this);
        var window = new Window(page) { Title = "DataPieMaui · Query results", Width = 1100, Height = 750, MinimumWidth = 640, MinimumHeight = 420 };
        window.Destroying += (_, _) =>
        {
            if (ReferenceEquals(resultWindow, window)) resultWindow = null;
            page.Release();
        };
        resultWindow = window;
        try { Application.Current!.OpenWindow(window); }
        catch { resultWindow = null; page.Release(); throw; }
    });
    public void CloseResults()
    {
        if (resultWindow is not { } window) return;
        resultWindow = null;
        Application.Current!.CloseWindow(window);
    }
}
