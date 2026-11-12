namespace DataPieMaui.Services.Shared;

internal sealed class BatchImportException(string message, string chineseMessage, Exception cause)
    : InvalidOperationException(message, cause)
{
    public string ChineseMessage { get; } = chineseMessage;
}

// A retry uses the original file list and resumes at the first uncommitted file.
internal sealed class BatchImportProgress(string[] files)
{
    private int next;
    public int Count => files.Length;

    public static BatchImportProgress Open(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Select an existing folder.");
        var paths = ImportFiles.GetFiles(folder).Select(file => file.FullName)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) throw new InvalidOperationException("The folder contains no CSV or XLSX files.");
        return new(paths);
    }

    public async Task RunAsync(Func<string, Task> import, Action<int, int, string>? progress = null)
    {
        while (next < files.Length)
        {
            string path = files[next];
            progress?.Invoke(next + 1, files.Length, Path.GetFileName(path));
            try { await import(path); }
            catch (Exception ex)
            {
                static string Names(IEnumerable<string> paths, string empty) => string.Join(", ", paths.Select(Path.GetFileName).DefaultIfEmpty(empty));
                var imported = files.Take(next);
                var pending = files.Skip(next + 1);
                string failed = Path.GetFileName(path);
                throw new BatchImportException(
                    $"Imported: {Names(imported, "None")}\nFailed: {failed}\nPending: {Names(pending, "None")}\n" +
                    $"Retry this folder and table in the same window to resume.\n{ex.Message}",
                    $"已导入：{Names(imported, "无")}\n失败：{failed}\n待处理：{Names(pending, "无")}\n" +
                    $"在同一窗口对同一文件夹和表重试，即可继续导入。\n{ex.Message}", ex);
            }
            next++;
        }
    }
}
