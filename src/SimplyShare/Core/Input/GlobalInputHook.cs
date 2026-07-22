using System.Runtime.InteropServices;
using SimplyShare.Core.Input;

namespace SimplyShare.Core.Input;

public sealed class GlobalInputHook : IDisposable
{
    private nint _mouseHook;
    private nint _keyboardHook;
    private HookProc? _mouseProc;
    private HookProc? _keyboardProc;
    private bool _disposed;

    /// <summary>로컬 입력 차단 여부 콜백 (true면 훅에서 입력 소비)</summary>
    public Func<bool>? ShouldBlockInput { get; set; }

    public event Action<int>? KeyDown;
    public event Action<int>? KeyUp;
    public event Action<int, int>? MouseMove;
    public event Action<int>? MouseWheel;
    public event Action<int>? MouseDown;
    public event Action<int>? MouseUp;

    public void Start()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(GlobalInputHook));

        if (_mouseHook != 0 || _keyboardHook != 0)
            return;

        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;

        // 저수준 훅은 호출 프로세스의 스레드에서 실행된다. Native AOT의 역호출
        // thunk는 PE 모듈에 내장된 훅 프로시저가 아니므로 hMod를 반드시 NULL로 둔다.
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, nint.Zero, 0);
        if (_mouseHook == 0)
            throw new InvalidOperationException($"SetWindowsHookEx(WH_MOUSE_LL) 실패: {Marshal.GetLastWin32Error()}");

        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, nint.Zero, 0);
        if (_keyboardHook == 0)
        {
            var err = Marshal.GetLastWin32Error();
            Stop();
            throw new InvalidOperationException($"SetWindowsHookEx(WH_KEYBOARD_LL) 실패: {err}");
        }
    }

    public void Stop()
    {
        if (_mouseHook != 0)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }

        if (_keyboardHook != 0)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
    }

    private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        var shouldBlock = false;
        var isInjectedBySimplyShare = false;
        var msg = 0;

        try
        {
            shouldBlock = ShouldBlockInput?.Invoke() is true;
            if (nCode >= 0)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                isInjectedBySimplyShare = info.dwExtraInfo == InputInjector.InjectedExtraInfo;
                msg = unchecked((int)wParam);
                switch (msg)
                {
                    case WM_MOUSEMOVE:
                        // 로컬 리센터링을 위한 주입 이벤트는 무시
                        if (!isInjectedBySimplyShare)
                            MouseMove?.Invoke(info.pt.x, info.pt.y);
                        break;
                    case WM_LBUTTONDOWN:
                        if (!isInjectedBySimplyShare)
                            MouseDown?.Invoke(1);
                        break;
                    case WM_LBUTTONUP:
                        if (!isInjectedBySimplyShare)
                            MouseUp?.Invoke(1);
                        break;
                    case WM_RBUTTONDOWN:
                        if (!isInjectedBySimplyShare)
                            MouseDown?.Invoke(2);
                        break;
                    case WM_RBUTTONUP:
                        if (!isInjectedBySimplyShare)
                            MouseUp?.Invoke(2);
                        break;
                    case WM_MBUTTONDOWN:
                        if (!isInjectedBySimplyShare)
                            MouseDown?.Invoke(3);
                        break;
                    case WM_MBUTTONUP:
                        if (!isInjectedBySimplyShare)
                            MouseUp?.Invoke(3);
                        break;
                    case WM_MOUSEWHEEL:
                        {
                            var delta = unchecked((short)((info.mouseData >> 16) & 0xFFFF));
                            if (!isInjectedBySimplyShare)
                                MouseWheel?.Invoke(delta);
                            break;
                        }
                }
            }
        }
        catch
        {
            // Native AOT 역호출 경계에서는 어떤 예외도 전파하지 않는다.
        }

        if (shouldBlock && !isInjectedBySimplyShare)
        {
            return 1;
        }

        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    private nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        var shouldBlock = false;
        var isInjectedBySimplyShare = false;

        try
        {
            shouldBlock = ShouldBlockInput?.Invoke() is true;
            if (nCode >= 0)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                isInjectedBySimplyShare = info.dwExtraInfo == InputInjector.InjectedExtraInfo;
                var msg = unchecked((int)wParam);
                switch (msg)
                {
                    case WM_KEYDOWN:
                    case WM_SYSKEYDOWN:
                        if (!isInjectedBySimplyShare)
                            KeyDown?.Invoke(unchecked((int)info.vkCode));
                        break;
                    case WM_KEYUP:
                    case WM_SYSKEYUP:
                        if (!isInjectedBySimplyShare)
                            KeyUp?.Invoke(unchecked((int)info.vkCode));
                        break;
                }
            }
        }
        catch
        {
            // Native AOT 역호출 경계에서는 어떤 예외도 전파하지 않는다.
        }

        if (shouldBlock && !isInjectedBySimplyShare)
            return 1;

        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _mouseProc = null;
        _keyboardProc = null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;

    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_MOUSEWHEEL = 0x020A;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

}
