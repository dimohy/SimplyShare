using System.Runtime.InteropServices;
using SimplyShare.Models;

namespace SimplyShare.Core.Input;

public static class InputInjector
{
    public static readonly nint InjectedExtraInfo = new(0x53485348); // 'SHSH'
    [ThreadStatic] private static INPUT[]? _singleInputBuffer;
    [ThreadStatic] private static INPUT[]? _batchInputBuffer;
    private static readonly int InputStructSize = Marshal.SizeOf<INPUT>();

    public static void Inject(InputEvent inputEvent)
    {
        try
        {
            var input = BuildInput(inputEvent);
            if (input.HasValue)
                Send(input.Value);
        }
        catch (Exception ex)
        {
            AppLogger.Log("InputInjector", $"입력 주입 실패: {ex}");
        }
    }

    public static void InjectBatch(IReadOnlyList<InputEvent> inputEvents)
    {
        if (inputEvents.Count == 0)
            return;

        if (inputEvents.Count == 1)
        {
            Inject(inputEvents[0]);
            return;
        }

        try
        {
            var inputs = _batchInputBuffer;
            if (inputs is null || inputs.Length < inputEvents.Count)
            {
                inputs = new INPUT[inputEvents.Count];
                _batchInputBuffer = inputs;
            }

            var count = 0;
            for (var i = 0; i < inputEvents.Count; i++)
            {
                var input = BuildInput(inputEvents[i]);
                if (!input.HasValue)
                    continue;

                inputs[count++] = input.Value;
            }

            if (count == 0)
                return;

            var sent = SendInput((uint)count, inputs, InputStructSize);
            if (sent == 0)
            {
                AppLogger.Log("InputInjector", $"SendInput 배치 실패: {Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log("InputInjector", $"배치 입력 주입 실패: {ex}");
        }
    }

    private static INPUT? BuildInput(InputEvent inputEvent)
    {
        return inputEvent.Kind switch
        {
            InputEventKind.MouseMove => BuildMouseMoveInput(inputEvent.Arg1, inputEvent.Arg2),
            InputEventKind.MouseDown => BuildMouseButtonInput(inputEvent.Arg1, true),
            InputEventKind.MouseUp => BuildMouseButtonInput(inputEvent.Arg1, false),
            InputEventKind.MouseWheel => BuildMouseWheelInput(inputEvent.Arg1),
            InputEventKind.KeyDown => BuildKeyInput(inputEvent.Arg1, true),
            InputEventKind.KeyUp => BuildKeyInput(inputEvent.Arg1, false),
            _ => null
        };
    }

    private static INPUT? BuildMouseMoveInput(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
            return null;

        if (!GetCursorPos(out var pt))
            return null;

        var bounds = ScreenInfo.VirtualScreen;
        var targetX = pt.X + dx;
        var targetY = pt.Y + dy;

        if (targetX < bounds.Left) targetX = bounds.Left;
        if (targetX > bounds.Right - 1) targetX = bounds.Right - 1;
        if (targetY < bounds.Top) targetY = bounds.Top;
        if (targetY > bounds.Bottom - 1) targetY = bounds.Bottom - 1;

        var width = Math.Max(1, bounds.Width);
        var height = Math.Max(1, bounds.Height);

        var normX = (int)Math.Round((targetX - bounds.Left) * 65535.0 / Math.Max(1, width - 1));
        var normY = (int)Math.Round((targetY - bounds.Top) * 65535.0 / Math.Max(1, height - 1));

        if (normX < 0) normX = 0;
        if (normX > 65535) normX = 65535;
        if (normY < 0) normY = 0;
        if (normY > 65535) normY = 65535;

        return new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = normX,
                    dy = normY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    dwExtraInfo = InjectedExtraInfo
                }
            }
        };
    }

    private static INPUT BuildMouseWheelInput(int delta)
    {
        return new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    mouseData = unchecked((uint)delta),
                    dwFlags = MOUSEEVENTF_WHEEL,
                    dwExtraInfo = InjectedExtraInfo
                }
            }
        };
    }

    private static INPUT? BuildMouseButtonInput(int button, bool isDown)
    {
        var flag = button switch
        {
            1 => isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            2 => isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            3 => isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => 0u
        };

        if (flag == 0)
            return null;

        return new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = flag,
                    dwExtraInfo = InjectedExtraInfo
                }
            }
        };
    }

    private static INPUT? BuildKeyInput(int virtualKey, bool isDown)
    {
        if (virtualKey is <= 0 or > 0xFFFF)
            return null;

        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)virtualKey,
                    dwFlags = isDown ? 0u : KEYEVENTF_KEYUP,
                    dwExtraInfo = InjectedExtraInfo
                }
            }
        };
    }

    private static void SendMouseMove(int dx, int dy)
    {
        var input = BuildMouseMoveInput(dx, dy);
        if (input.HasValue)
            Send(input.Value);
    }

    private static void SendMouseWheel(int delta)
    {
        Send(BuildMouseWheelInput(delta));
    }

    private static void SendMouseButton(int button, bool isDown)
    {
        var input = BuildMouseButtonInput(button, isDown);
        if (input.HasValue)
            Send(input.Value);
    }

    private static void SendKey(int virtualKey, bool isDown)
    {
        var input = BuildKeyInput(virtualKey, isDown);
        if (input.HasValue)
            Send(input.Value);
    }

    private static void Send(INPUT input)
    {
        var inputs = _singleInputBuffer ??= new INPUT[1];
        inputs[0] = input;

        var sent = SendInput(1, inputs, InputStructSize);
        if (sent == 0)
        {
            AppLogger.Log("InputInjector", $"SendInput 실패: {Marshal.GetLastWin32Error()}");
        }
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }
}
