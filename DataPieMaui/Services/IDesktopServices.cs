namespace DataPieMaui.Services;

// Native operations stay behind this boundary; workspace logic can run without MAUI.
public interface IDesktopServices
{
    Task<string?> OpenAsync(params string[] extensions);
    Task<string?> SaveAsync(string name, string extension);
    // Selects a destination without creating or modifying any file.
    Task<string?> SelectNewFilePathAsync(string name, string extension);
    Task<string?> FolderAsync();
    Task CopyTextAsync(string text);
    Task ShowTablePreviewAsync(QueryResultSession session);
    Task ShowTableEditorAsync(DataPieCore.TableEditorSession session, string connectionLabel);
    Task ShowDataAnalysisAsync(Shared.DataAnalysisSession session);
}
