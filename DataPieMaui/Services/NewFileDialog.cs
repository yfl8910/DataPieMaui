using System.Runtime.InteropServices;

namespace DataPieMaui.Services;

// GetSaveFileName selects a path without creating the destination file.
internal static class NewFileDialog
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int Size;
        public nint Owner, Instance;
        public string? Filter, CustomFilter;
        public int MaxCustomFilter, FilterIndex;
        public nint File;
        public int MaxFile;
        public nint FileTitle;
        public int MaxFileTitle;
        public string? InitialDirectory, Title;
        public int Flags;
        public short FileOffset, FileExtension;
        public string? DefaultExtension;
        public nint CustomData, Hook;
        public string? TemplateName;
        public nint Reserved;
        public int ReservedInt, FlagsEx;
    }
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetSaveFileNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileName(ref OpenFileName dialog);
    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();

    public static string? Select(nint owner, string name, string extension)
    {
        const int capacity = 32768;
        var buffer = Marshal.AllocHGlobal(capacity * sizeof(char));
        try
        {
            var initial = new char[capacity];
            var filename = Shared.FileNames.SafeFileName(name) + extension;
            filename.CopyTo(0, initial, 0, Math.Min(filename.Length, capacity - 1));
            Marshal.Copy(initial, 0, buffer, capacity);
            var dialog = new OpenFileName
            {
                Size = Marshal.SizeOf<OpenFileName>(), Owner = owner,
                Filter = $"SQLite database (*{extension})\0*{extension}\0\0", FilterIndex = 1,
                File = buffer, MaxFile = capacity, Title = "Create blank SQLite database",
                DefaultExtension = extension.TrimStart('.'),
                // Explorer, no current-directory change, existing directory, overwrite prompt.
                Flags = 0x80000 | 0x8 | 0x800 | 0x2
            };
            if (GetSaveFileName(ref dialog)) return Marshal.PtrToStringUni(buffer);
            var error = CommDlgExtendedError();
            if (error != 0) throw new InvalidOperationException($"The save dialog failed (0x{error:X}).");
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
