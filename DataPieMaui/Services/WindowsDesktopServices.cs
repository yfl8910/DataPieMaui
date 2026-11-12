namespace DataPieMaui.Services;

internal sealed class WindowsDesktopServices(Func<Window?> owner, Func<string> language) : IDesktopServices
{
    private Window? resultWindow;
    private readonly Dictionary<Window, DataPieCore.TableEditorSession> editorWindows = [];
    private readonly Dictionary<Window, Shared.DataAnalysisSession> analysisWindows = [];
    public bool HasPendingTableEdits => editorWindows.Values.Any(s => s.HasDraft || s.IsBusy) || analysisWindows.Values.Any(s => s.Busy);
    private Window Owner => owner() ?? throw new InvalidOperationException("The application window is not ready.");
    public Task<string?> OpenAsync(params string[] extensions) => MainThread.InvokeOnMainThreadAsync(() => WindowsFiles.OpenAsync(Owner, extensions));
    public Task<string?> SaveAsync(string name, string extension) => MainThread.InvokeOnMainThreadAsync(() => WindowsFiles.SaveAsync(Owner, name, extension));
    public Task<string?> SelectNewFilePathAsync(string name, string extension) => MainThread.InvokeOnMainThreadAsync(() => NewFileDialog.Select(WinRT.Interop.WindowNative.GetWindowHandle(Owner.Handler?.PlatformView ?? throw new InvalidOperationException("The application window is not ready.")), name, extension));
    public Task<string?> FolderAsync() => MainThread.InvokeOnMainThreadAsync(() => WindowsFiles.FolderAsync(Owner));
    public Task CopyTextAsync(string text) => MainThread.InvokeOnMainThreadAsync(() => Clipboard.Default.SetTextAsync(text));
    public Task ShowTablePreviewAsync(QueryResultSession session) => MainThread.InvokeOnMainThreadAsync(() =>
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
        string locale = language();
        var page = new Controls.TableEditorHostPage(session, this, locale);
        var window = new Window(page) { Title = locale == "zh-CN" ? "DataPieMaui · 只读预览" : "DataPieMaui · Table editor · Read-only preview", Width = 1100, Height = 750, MinimumWidth = 640, MinimumHeight = 420 };
        window.Destroying += (_, _) =>
        {
            if (ReferenceEquals(resultWindow, window)) resultWindow = null;
            session.Current?.CancelCapture();
            page.Release();
        };
        resultWindow = window;
        try { Application.Current!.OpenWindow(window); }
        catch { resultWindow = null; page.Release(); throw; }
    });
    public Task ShowTableEditorAsync(DataPieCore.TableEditorSession session, string connectionLabel) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        string locale = language();
        var page = new Controls.TableEditorHostPage(session, connectionLabel, this, locale);
        var window = new Window(page) { Title = "DataPieMaui · " + page.Title + " · " + session.TableName, Width = 1100, Height = 750, MinimumWidth = 640, MinimumHeight = 420 };
        bool allowClose = false, confirming = false;
        window.Created += (_, _) =>
        {
            if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
                native.AppWindow.Closing += async (_, args) =>
                {
                    if (allowClose) return;
                    if (session.IsBusy) { args.Cancel = true; return; }
                    if (!session.HasDraft) return;
                    args.Cancel = true;
                    if (confirming) return;
                    confirming = true;
                    try
                    {
                        bool chinese = locale == "zh-CN";
                        if (await page.DisplayAlertAsync(chinese ? "未保存的更改" : "Unsaved changes",
                            chinese ? "放弃草稿并关闭表编辑器吗？" : "Discard the draft and close the table editor?",
                            chinese ? "放弃" : "Discard", chinese ? "继续编辑" : "Keep editing"))
                        {
                            allowClose = true;
                            Application.Current!.CloseWindow(window);
                        }
                    }
                    finally { confirming = false; }
                };
        };
        window.Destroying += (_, _) => { editorWindows.Remove(window); page.Release(); session.Data.Dispose(); };
        editorWindows.Add(window, session);
        try { Application.Current!.OpenWindow(window); }
        catch { editorWindows.Remove(window); page.Release(); session.Data.Dispose(); throw; }
    });
    public Task ShowDataAnalysisAsync(Shared.DataAnalysisSession session) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        var page = new Controls.DataAnalysisHostPage(session, this, language());
        var window = new Window(page) { Title = "DataPieMaui · " + page.Title + " · " + session.Source.TableName, Width = 1200, Height = 850, MinimumWidth = 640, MinimumHeight = 500 };
        window.Created += (_, _) =>
        {
            if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
                native.AppWindow.Closing += (_, args) => { if (session.Busy) args.Cancel = true; };
        };
        window.Destroying += (_, _) => { analysisWindows.Remove(window); page.Release(); };
        analysisWindows.Add(window, session);
        try { Application.Current!.OpenWindow(window); }
        catch { analysisWindows.Remove(window); page.Release(); throw; }
    });

    public void CloseResults()
    {
        foreach (var analysis in analysisWindows.Keys.ToArray()) Application.Current!.CloseWindow(analysis);
        foreach (var editor in editorWindows.Keys.ToArray()) Application.Current!.CloseWindow(editor);
        if (resultWindow is not { } window) return;
        resultWindow = null;
        Application.Current!.CloseWindow(window);
    }
}
