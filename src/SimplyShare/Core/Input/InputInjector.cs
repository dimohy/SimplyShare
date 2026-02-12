using System.Runtime.InteropServices;
using SimplyShare.Models;

namespace SimplyShare.Core.Input;

public static class InputInjector
{
    public static readonly nint InjectedExtraInfo = new(0x53485348); // 'SHSH'

    public static void Inject(InputEvent inputEvent)
    {
        try
        {
            switch (inputEvent.Kind)
            {
                case InputEventKind.MouseMove:
                    SendMouseMove(inputEvent.Arg1, inputEvent.Arg2);
                    break;
                case InputEventKind.MouseDown:
                    SendMouseButton(inputEvent.Arg1, isDown: true);
                    break;
                case InputEventKind.MouseUp:
                    SendMouseButton(inputEvent.Arg1, isDown: false);
                    break;
                case InputEventKind.MouseWheel:
                    SendMouseWheel(inputEvent.Arg1);
                    break;
                case InputEventKind.KeyDown:
                    SendKey(inputEvent.Arg1, isDown: true);
                    break;
                case InputEventKind.KeyUp:
                    SendKey(inputEvent.Arg1, isDown: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log("InputInjector", $"입력 주입 실패: {ex}");
        }
    }

    private static void SendMouseMove(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
            return;

        // 상대 이동 주입은 시스템 마우스 속도/가속 영향을 크게 받아 체감이 느릴 수 있다.
        // 따라서 현재 커서 좌표를 기준으로 목표 좌표를 계산한 뒤 ABSOLUTE로 주입한다.
        if (!GetCursorPos(out var pt))
            return;

        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        var targetX = pt.X + dx;
        var targetY = pt.Y + dy;

        // VirtualScreen 범위 내로 클램프
        if (targetX < bounds.Left) targetX = bounds.Left;
        if (targetX > bounds.Right - 1) targetX = bounds.Right - 1;
        if (targetY < bounds.Top) targetY = bounds.Top;
        if (targetY > bounds.Bottom - 1) targetY = bounds.Bottom - 1;

        var width = Math.Max(1, bounds.Width);
        var height = Math.Max(1, bounds.Height);

        // 0..65535 정규화 (절대좌표)
        var normX = (int)Math.Round((targetX - bounds.Left) * 65535.0 / Math.Max(1, width - 1));
        var normY = (int)Math.Round((targetY - bounds.Top) * 65535.0 / Math.Max(1, height - 1));

        if (normX < 0) normX = 0;
        if (normX > 65535) normX = 65535;
        if (normY < 0) normY = 0;
        if (normY > 65535) normY = 65535;

        Send(new INPUT
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
        });
    }

    private static void SendMouseWheel(int delta)
    {
        Send(new INPUT
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
        });
    }

    private static void SendMouseButton(int button, bool isDown)
    {
        var flag = button switch
        {
            1 => isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            2 => isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            3 => isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => 0u
        };

        if (flag == 0)
            return;

        Send(new INPUT
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
        });
    }

    private static void SendKey(int virtualKey, bool isDown)
    {
        if (virtualKey is <= 0 or > 0xFFFF)
            return;

        Send(new INPUT
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
        });
    }

    private static void Send(INPUT input)
    {
        var inputs = new[] { input };
        var sent = SendInput(1, inputs, Marshal.SizeOf<INPUT>());
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
