using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SimplyShare.AppHost;

internal static class NativeFileDialog
{
    private const int OfnExplorer = 0x00080000;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnAllowMultiSelect = 0x00000200;
    private const int OfnNoChangeDir = 0x00000008;

    public static IReadOnlyList<string> PickFiles(string title)
    {
        var buffer = new StringBuilder(65_536);
        _ = buffer.Append('\0', 65_535);
        var filter = "모든 파일\0*.*\0\0";
        var dialog = new OpenFileName
        {
            StructSize = Marshal.SizeOf<OpenFileName>(),
            Filter = filter,
            File = buffer,
            MaxFile = buffer.Length,
            FileTitle = new StringBuilder(260),
            MaxFileTitle = 260,
            Title = title,
            Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnAllowMultiSelect | OfnNoChangeDir,
        };

        if (!GetOpenFileName(ref dialog))
        {
            var error = CommDlgExtendedError();
            return error is 0
                ? Array.Empty<string>()
                : throw new Win32Exception((int)error);
        }

        return ParseSelection(dialog.File.ToString());
    }

    private static IReadOnlyList<string> ParseSelection(string rawBuffer)
    {
        var parts = rawBuffer
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length is 0)
        {
            return Array.Empty<string>();
        }

        if (parts.Length is 1)
        {
            return [parts[0]];
        }

        var directory = parts[0];
        var results = new string[parts.Length - 1];
        for (var i = 1; i < parts.Length; i++)
        {
            results[i - 1] = Path.Combine(directory, parts[i]);
        }

        return results;
    }

    [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName openFileName);

    [DllImport("comdlg32.dll")]
    private static extern uint CommDlgExtendedError();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public nint Owner;
        public nint Instance;
        public string Filter;
        public string? CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public StringBuilder File;
        public int MaxFile;
        public StringBuilder FileTitle;
        public int MaxFileTitle;
        public string? InitialDir;
        public string? Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public string? DefExt;
        public nint CustData;
        public nint Hook;
        public string? TemplateName;
        public nint ReservedPtr;
        public int ReservedInt;
        public int FlagsEx;
    }
}