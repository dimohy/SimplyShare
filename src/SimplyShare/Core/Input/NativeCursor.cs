using System.Runtime.InteropServices;

namespace SimplyShare.Core.Input;

internal static class NativeCursor
{
    public static bool TryGetCursorPosition(out int x, out int y)
    {
        if (GetCursorPos(out var pt))
        {
            x = pt.x;
            y = pt.y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    public static void ClearCursorHandle()
    {
        _ = SetCursor(nint.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint hCursor);
}
