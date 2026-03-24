using System.Runtime.InteropServices;

namespace SimplyShare.Core.Input;

public sealed class RawInputHook : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const int WM_DESTROY = 0x0002;

    private nint _hwnd;
    private Thread? _messageThread;
    private bool _started;
    private bool _hasAbsoluteBaseline;
    private int _lastAbsoluteX;
    private int _lastAbsoluteY;
    private nint _rawInputBuffer;
    private int _rawInputBufferSize;
    private WndProcDelegate? _wndProcDelegate;

    public event Action<int, int>? MouseDelta;
    public event Action<int>? MouseWheel;
    public event Action<int>? MouseDown;
    public event Action<int>? MouseUp;
    public event Action<int>? KeyDown;
    public event Action<int>? KeyUp;

    public void Start()
    {
        if (_started)
            return;

        _started = true;

        var ready = new ManualResetEventSlim(false);

        _messageThread = new Thread(() => MessageThreadProc(ready))
        {
            IsBackground = true,
            Name = "RawInputHook"
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();

        ready.Wait();
    }

    public void Stop()
    {
        if (!_started)
            return;

        _started = false;

        if (_hwnd != 0)
        {
            PostMessage(_hwnd, WM_DESTROY, 0, 0);
        }

        _messageThread?.Join(2000);
        _messageThread = null;
        _hwnd = 0;
        _hasAbsoluteBaseline = false;
        ReleaseRawInputBuffer();
    }

    private void MessageThreadProc(ManualResetEventSlim ready)
    {
        _wndProcDelegate = WndProc;

        var wndClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProcDelegate,
            hInstance = GetModuleHandle(null),
            lpszClassName = "SimplyShareRawInput_" + Environment.TickCount64
        };

        var atom = RegisterClassEx(ref wndClass);
        if (atom == 0)
        {
            AppLogger.Log("RawInput", $"RegisterClassEx 실패: {Marshal.GetLastWin32Error()}");
            ready.Set();
            return;
        }

        _hwnd = CreateWindowEx(
            0, wndClass.lpszClassName, string.Empty, 0,
            -200, -200, 0, 0,
            HWND_MESSAGE, 0, wndClass.hInstance, 0);

        if (_hwnd == 0)
        {
            AppLogger.Log("RawInput", $"CreateWindowEx 실패: {Marshal.GetLastWin32Error()}");
            ready.Set();
            return;
        }

        RegisterDevices(_hwnd);
        ready.Set();

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        DestroyWindow(_hwnd);
        UnregisterClass(wndClass.lpszClassName, wndClass.hInstance);
        _hwnd = 0;
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_INPUT)
        {
            try
            {
                ProcessRawInput(lParam);
            }
            catch
            {
                // 메시지 루프에서 예외 전파 금지
            }

            return 0;
        }

        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ProcessRawInput(nint hRawInput)
    {
        uint dwSize = 0;
        _ = GetRawInputData(hRawInput, RID_INPUT, nint.Zero, ref dwSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (dwSize == 0)
            return;

        EnsureRawInputBufferCapacity((int)dwSize);

        var read = GetRawInputData(hRawInput, RID_INPUT, _rawInputBuffer, ref dwSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (read == 0 || read != dwSize)
            return;

        var raw = Marshal.PtrToStructure<RAWINPUT>(_rawInputBuffer);
        switch (raw.header.dwType)
        {
            case RIM_TYPEMOUSE:
                HandleMouse(raw.data.mouse);
                break;
            case RIM_TYPEKEYBOARD:
                HandleKeyboard(raw.data.keyboard);
                break;
        }
    }

    private void EnsureRawInputBufferCapacity(int requiredSize)
    {
        if (requiredSize <= _rawInputBufferSize && _rawInputBuffer != nint.Zero)
            return;

        _rawInputBuffer = _rawInputBuffer == nint.Zero
            ? Marshal.AllocHGlobal(requiredSize)
            : Marshal.ReAllocHGlobal(_rawInputBuffer, (nint)requiredSize);
        _rawInputBufferSize = requiredSize;
    }

    private void ReleaseRawInputBuffer()
    {
        if (_rawInputBuffer == nint.Zero)
            return;

        Marshal.FreeHGlobal(_rawInputBuffer);
        _rawInputBuffer = nint.Zero;
        _rawInputBufferSize = 0;
    }

    private void HandleMouse(RAWMOUSE mouse)
    {
        var dx = mouse.lLastX;
        var dy = mouse.lLastY;

        if ((mouse.usFlags & MOUSE_MOVE_ABSOLUTE) != 0)
        {
            var bounds = (mouse.usFlags & MOUSE_VIRTUAL_DESKTOP) != 0
                ? ScreenInfo.VirtualScreen
                : ScreenInfo.PrimaryMonitorBounds;

            var absX = bounds.Left + (int)Math.Round(mouse.lLastX * (bounds.Width / 65535.0));
            var absY = bounds.Top + (int)Math.Round(mouse.lLastY * (bounds.Height / 65535.0));

            if (_hasAbsoluteBaseline)
            {
                dx = absX - _lastAbsoluteX;
                dy = absY - _lastAbsoluteY;
            }
            else
            {
                dx = 0;
                dy = 0;
                _hasAbsoluteBaseline = true;
            }

            _lastAbsoluteX = absX;
            _lastAbsoluteY = absY;
        }

        if (dx != 0 || dy != 0)
            MouseDelta?.Invoke(dx, dy);

        var flags = mouse.ButtonData.usButtonFlags;
        if ((flags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0) MouseDown?.Invoke(1);
        if ((flags & RI_MOUSE_LEFT_BUTTON_UP) != 0) MouseUp?.Invoke(1);
        if ((flags & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0) MouseDown?.Invoke(2);
        if ((flags & RI_MOUSE_RIGHT_BUTTON_UP) != 0) MouseUp?.Invoke(2);
        if ((flags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0) MouseDown?.Invoke(3);
        if ((flags & RI_MOUSE_MIDDLE_BUTTON_UP) != 0) MouseUp?.Invoke(3);

        if ((flags & RI_MOUSE_WHEEL) != 0)
        {
            var delta = unchecked((short)mouse.ButtonData.usButtonData);
            MouseWheel?.Invoke(delta);
        }
    }

    private void HandleKeyboard(RAWKEYBOARD keyboard)
    {
        var vk = unchecked((int)keyboard.VKey);
        if (vk == 0)
            return;

        var isBreak = (keyboard.Flags & RI_KEY_BREAK) != 0;
        if (isBreak)
            KeyUp?.Invoke(vk);
        else
            KeyDown?.Invoke(vk);
    }

    private static void RegisterDevices(nint hwnd)
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x02, // Mouse
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = hwnd
            },
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x06, // Keyboard
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = hwnd
            }
        };

        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            AppLogger.Log("RawInput", $"RegisterRawInputDevices 실패: {Marshal.GetLastWin32Error()}");
        }
    }

    public void Dispose() => Stop();

    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEMOUSE = 0;
    private const uint RIM_TYPEKEYBOARD = 1;

    private const uint RIDEV_INPUTSINK = 0x00000100;

    private const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
    private const ushort RI_MOUSE_LEFT_BUTTON_UP = 0x0002;
    private const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
    private const ushort RI_MOUSE_RIGHT_BUTTON_UP = 0x0008;
    private const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;
    private const ushort RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020;
    private const ushort RI_MOUSE_WHEEL = 0x0400;

    private const ushort RI_KEY_BREAK = 0x0001;
    private const ushort MOUSE_MOVE_ABSOLUTE = 0x0001;
    private const ushort MOUSE_VIRTUAL_DESKTOP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public nint hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public nint hDevice;
        public nint wParam;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RAWINPUTDATA
    {
        [FieldOffset(0)] public RAWMOUSE mouse;
        [FieldOffset(0)] public RAWKEYBOARD keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWINPUTDATA data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWMOUSE
    {
        public ushort usFlags;
        public RAWMOUSEBUTTONS ButtonData;
        public uint ulRawButtons;
        public int lLastX;
        public int lLastY;
        public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RAWMOUSEBUTTONS
    {
        [FieldOffset(0)] public uint ulButtons;
        [FieldOffset(0)] public ushort usButtonFlags;
        [FieldOffset(2)] public ushort usButtonData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        nint hRawInput,
        uint uiCommand,
        nint pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    // ── 윈도우 생성/메시지 루프 ──

    private static readonly nint HWND_MESSAGE = new(-3);

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        [MarshalAs(UnmanagedType.FunctionPtr)]
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);
}
