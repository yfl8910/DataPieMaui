namespace DataPieMaui.Services.Shared;

public static class FileNames
{
    public static string SafeFileName(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(safe) ? "export" : safe;
    }
}
