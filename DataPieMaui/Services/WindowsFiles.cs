namespace DataPieMaui.Services;

internal static class WindowsFiles
{
    private static nint WindowHandle(Window owner) => WinRT.Interop.WindowNative.GetWindowHandle(
        owner.Handler?.PlatformView ?? throw new InvalidOperationException("The application window is not ready."));

    public static async Task<string?> OpenAsync(Window owner, params string[] extensions)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle(owner));
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public static async Task<string?> SaveAsync(Window owner, string name, string extension)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = WorkspaceController.SafeFileName(name) };
        picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(), new[] { extension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle(owner));
        return (await picker.PickSaveFileAsync())?.Path;
    }

    public static async Task<string?> FolderAsync(Window owner)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle(owner));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

}
