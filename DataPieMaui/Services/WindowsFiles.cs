namespace DataPieMaui.Services;

internal static class WindowsFiles
{
    private static nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(
        Application.Current!.Windows[0].Handler!.PlatformView);

    public static async Task<string?> OpenAsync(params string[] extensions)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public static async Task<string?> SaveAsync(string name, string extension)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = SafeName(name) };
        picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(), new[] { extension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    public static async Task<string?> FolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public static string SafeName(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(safe) ? "export" : safe;
    }
}
