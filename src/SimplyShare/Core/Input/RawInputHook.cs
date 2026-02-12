using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SimplyShare.Core.Input;

public sealed class RawInputHook : IDisposable
{
    private const int WM_INPUT = 0x00FF;

    private HwndSource? _source;
    private nint _hwnd;
    private bool _started;
    private bool _hasAbsoluteBaseline;
    private int _lastAbsoluteX;
    private int _lastAbsoluteY;

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

        var parameters = new HwndSourceParameters("SimplyShareRawInput")
        {
            Width = 0,
            Height = 0,
            PositionX = -200,
            PositionY = -200,
            WindowStyle = 0
        };

        _source = new HwndSource(parameters);
        _hwnd = _source.Handle;
        _source.AddHook(WndProc);

        RegisterDevices(_hwnd);
        _started = true;
    }

    public void Stop()
    {
        if (!_started)
            return;

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }

        _hwnd = 0;
        _started = false;
        _hasAbsoluteBaseline = false;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_INPUT)
        {
            handled = true;
            try
            {
                ProcessRawInput(lParam);
            }
            catch
            {
                // 메시지 루프에서 예외 전파 금지
            }
        }

        return 0;
    }

    private void ProcessRawInput(nint hRawInput)
    {
        uint dwSize = 0;
        _ = GetRawInputData(hRawInput, RID_INPUT, nint.Zero, ref dwSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (dwSize == 0)
            return;

        var buffer = Marshal.AllocHGlobal((int)dwSize);
        try
        {
            var read = GetRawInputData(hRawInput, RID_INPUT, buffer, ref dwSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
            if (read == 0 || read != dwSize)
                return;

            var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);
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
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void HandleMouse(RAWMOUSE mouse)
    {
        var dx = mouse.lLastX;
        var dy = mouse.lLastY;

        if ((mouse.usFlags & MOUSE_MOVE_ABSOLUTE) != 0)
        {
            var bounds = (mouse.usFlags & MOUSE_VIRTUAL_DESKTOP) != 0
                ? System.Windows.Forms.SystemInformation.VirtualScreen
                : System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);

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
}
