using System.Runtime.InteropServices;

namespace SimplyShare.AppHost;

internal static class NativeTrayNotification
{
    private const uint NimModify = 0x00000001;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;

    public static void Show(nint windowHandle, string title, string message)
    {
        if (windowHandle == nint.Zero)
        {
            return;
        }

        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = windowHandle,
            uID = 1,
            uFlags = NifInfo,
            szInfo = Truncate(message, 255),
            szInfoTitle = Truncate(title, 63),
            dwInfoFlags = NiifInfo,
        };

        _ = ShellNotifyIcon(NimModify, ref data);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
}
