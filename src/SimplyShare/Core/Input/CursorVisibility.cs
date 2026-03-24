using System.Runtime.InteropServices;

namespace SimplyShare.Core.Input;

internal static class CursorVisibility
{
    private static int _hideDepth;

    public static void Hide()
    {
        if (_hideDepth++ > 0)
            return;

        try
        {
            while (ShowCursor(false) >= 0)
            {
                // ShowCursor 카운터를 음수로 만들어 실제 커서 숨김
            }
        }
        catch
        {
            // ignore
        }
    }

    public static void Show()
    {
        if (_hideDepth <= 0)
        {
            _hideDepth = 0;
            return;
        }

        if (--_hideDepth > 0)
            return;

        try
        {
            while (ShowCursor(true) < 0)
            {
                // ShowCursor 카운터를 0 이상으로 복원
            }
        }
        catch
        {
            // ignore
        }
    }

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);
}
