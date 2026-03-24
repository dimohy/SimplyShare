using System.Drawing;
using System.Runtime.InteropServices;

namespace SimplyShare.Core.Input;

/// <summary>모니터/화면 정보 — WinForms/WPF 의존 없이 순수 P/Invoke 구현</summary>
internal static class ScreenInfo
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    public static Rectangle VirtualScreen
    {
        get
        {
            var x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var cx = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var cy = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            return new Rectangle(x, y, cx, cy);
        }
    }

    public static Rectangle GetMonitorBoundsFromPoint(int x, int y)
    {
        var hMonitor = MonitorFromPoint(new POINT { x = x, y = y }, MONITOR_DEFAULTTONEAREST);
        if (hMonitor == 0)
            return new Rectangle(0, 0, 1920, 1080);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref info))
            return new Rectangle(0, 0, 1920, 1080);

        return new Rectangle(
            info.rcMonitor.left,
            info.rcMonitor.top,
            info.rcMonitor.right - info.rcMonitor.left,
            info.rcMonitor.bottom - info.rcMonitor.top);
    }

    public static Rectangle PrimaryMonitorBounds
    {
        get
        {
            var hMonitor = MonitorFromPoint(new POINT { x = 0, y = 0 }, MONITOR_DEFAULTTONEAREST);
            if (hMonitor == 0)
                return new Rectangle(0, 0, 1920, 1080);

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonitor, ref info))
                return new Rectangle(0, 0, 1920, 1080);

            return new Rectangle(
                info.rcMonitor.left,
                info.rcMonitor.top,
                info.rcMonitor.right - info.rcMonitor.left,
                info.rcMonitor.bottom - info.rcMonitor.top);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, int dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);
}
