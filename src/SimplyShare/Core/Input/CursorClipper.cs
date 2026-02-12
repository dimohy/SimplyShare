using System.Runtime.InteropServices;

namespace SimplyShare.Core.Input;

public static class CursorClipper
{
    public static void ClipToEdge(System.Drawing.Rectangle bounds, Models.BoundarySide side)
    {
        var rect = side switch
        {
            Models.BoundarySide.Right => new RECT
            {
                left = bounds.Right - 2,
                top = bounds.Top,
                right = bounds.Right - 1,
                bottom = bounds.Bottom
            },
            Models.BoundarySide.Left => new RECT
            {
                left = bounds.Left,
                top = bounds.Top,
                right = bounds.Left + 1,
                bottom = bounds.Bottom
            },
            Models.BoundarySide.Top => new RECT
            {
                left = bounds.Left,
                top = bounds.Top,
                right = bounds.Right,
                bottom = bounds.Top + 1
            },
            Models.BoundarySide.Bottom => new RECT
            {
                left = bounds.Left,
                top = bounds.Bottom - 2,
                right = bounds.Right,
                bottom = bounds.Bottom - 1
            },
            _ => new RECT
            {
                left = bounds.Right - 2,
                top = bounds.Top,
                right = bounds.Right - 1,
                bottom = bounds.Bottom
            }
        };

        _ = ClipCursor(ref rect);
    }

    public static void Release()
    {
        _ = ClipCursor(nint.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClipCursor(ref RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClipCursor(nint lpRect);
}
