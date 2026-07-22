using System.ComponentModel;
using System.Runtime.InteropServices;
using SimplyShare.Core;

namespace SimplyShare.AppHost;

internal sealed class NativeRemoteCursorOverlay : IDisposable
{
    private const int OverlaySize = 34;
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExNoActivate = 0x08000000;
    private const uint LwaColorKey = 0x00000001;
    private const uint SwHide = 0;
    private const uint SwShowNoActivate = 4;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint WmPaint = 0x000F;
    private const uint WmEraseBackground = 0x0014;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmOverlayShow = 0x8001;
    private const uint WmOverlayHide = 0x8002;
    private const uint WmOverlayMove = 0x8003;
    private const int PsSolid = 0;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly uint TransparentColor = Rgb(1, 2, 3);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WindowProcedure _windowProcedure;
    private nint _windowHandle;
    private string? _className;
    private int _creationError;
    private int _disposed;

    public NativeRemoteCursorOverlay()
    {
        _windowProcedure = WindowProc;
        _thread = new Thread(WindowThread)
        {
            IsBackground = true,
            Name = "SimplyShareRemoteCursorOverlay",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();

        if (_windowHandle == nint.Zero)
        {
            throw new Win32Exception(_creationError, "원격 커서 오버레이 창을 만들지 못했습니다.");
        }
    }

    public void Show()
    {
        PostOverlayMessage(WmOverlayShow);
    }

    public void Hide()
    {
        PostOverlayMessage(WmOverlayHide);
    }

    public void UpdatePosition()
    {
        PostOverlayMessage(WmOverlayMove);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_windowHandle != nint.Zero) _ = PostMessageW(_windowHandle, WmClose, nuint.Zero, nint.Zero);
        _thread.Join(2_000);
        _ready.Dispose();
    }

    private void WindowThread()
    {
        _className = $"SimplyShareRemoteCursor_{Environment.ProcessId}_{Environment.TickCount64}";
        var instance = GetModuleHandleW(null);
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            WindowProcedure = _windowProcedure,
            Instance = instance,
            ClassName = _className,
        };

        if (RegisterClassExW(ref windowClass) == 0)
        {
            _creationError = Marshal.GetLastWin32Error();
            _ready.Set();
            return;
        }

        _windowHandle = CreateWindowExW(
            WsExTopmost | WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate,
            _className,
            string.Empty,
            WsPopup,
            0,
            0,
            OverlaySize,
            OverlaySize,
            nint.Zero,
            nint.Zero,
            instance,
            nint.Zero);

        if (_windowHandle != nint.Zero)
        {
            _ = SetLayeredWindowAttributes(_windowHandle, TransparentColor, 255, LwaColorKey);
        }
        else
        {
            _creationError = Marshal.GetLastWin32Error();
        }

        _ready.Set();
        while (_windowHandle != nint.Zero && GetMessageW(out var message, nint.Zero, 0, 0) > 0)
        {
            _ = TranslateMessage(ref message);
            _ = DispatchMessageW(ref message);
        }

        if (_className is not null) _ = UnregisterClassW(_className, instance);
    }

    private nint WindowProc(nint windowHandle, uint message, nuint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case WmEraseBackground:
                    return new nint(1);
                case WmPaint:
                    Paint(windowHandle);
                    return nint.Zero;
                case WmOverlayShow:
                    UpdatePositionCore(windowHandle);
                    _ = ShowWindow(windowHandle, SwShowNoActivate);
                    _ = InvalidateRect(windowHandle, nint.Zero, false);
                    return nint.Zero;
                case WmOverlayHide:
                    _ = ShowWindow(windowHandle, SwHide);
                    return nint.Zero;
                case WmOverlayMove:
                    UpdatePositionCore(windowHandle);
                    return nint.Zero;
                case WmClose:
                    _ = DestroyWindow(windowHandle);
                    return nint.Zero;
                case WmDestroy:
                    _windowHandle = nint.Zero;
                    PostQuitMessage(0);
                    return nint.Zero;
                default:
                    return DefWindowProcW(windowHandle, message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            try
            {
                AppLogger.Log(nameof(NativeRemoteCursorOverlay), $"오버레이 창 메시지 처리 실패(0x{message:X}): {ex}");
            }
            catch
            {
                // 네이티브 콜백 경계에서는 어떤 예외도 전파하지 않는다.
            }

            return DefWindowProcW(windowHandle, message, wParam, lParam);
        }
    }

    private void PostOverlayMessage(uint message)
    {
        var windowHandle = Interlocked.CompareExchange(ref _windowHandle, nint.Zero, nint.Zero);
        if (windowHandle != nint.Zero && Volatile.Read(ref _disposed) == 0)
        {
            _ = PostMessageW(windowHandle, message, nuint.Zero, nint.Zero);
        }
    }

    private static void UpdatePositionCore(nint windowHandle)
    {
        if (!GetCursorPos(out var point)) return;
        _ = SetWindowPos(
            windowHandle,
            HwndTopmost,
            point.X - (OverlaySize / 2),
            point.Y - (OverlaySize / 2),
            OverlaySize,
            OverlaySize,
            SwpNoActivate | SwpShowWindow);
    }

    private static void Paint(nint windowHandle)
    {
        var dc = BeginPaint(windowHandle, out var paint);
        if (dc == nint.Zero) return;

        var transparentBrush = CreateSolidBrush(TransparentColor);
        _ = FillRect(dc, ref paint.PaintRect, transparentBrush);
        _ = DeleteObject(transparentBrush);

        var pen = CreatePen(PsSolid, 3, Rgb(255, 69, 0));
        var brush = CreateSolidBrush(Rgb(255, 178, 145));
        var oldPen = SelectObject(dc, pen);
        var oldBrush = SelectObject(dc, brush);
        _ = Ellipse(dc, 3, 3, OverlaySize - 3, OverlaySize - 3);
        _ = SelectObject(dc, oldPen);
        _ = SelectObject(dc, oldBrush);
        _ = DeleteObject(pen);
        _ = DeleteObject(brush);
        _ = EndPaint(windowHandle, ref paint);
    }

    private static uint Rgb(byte red, byte green, byte blue)
        => red | ((uint)green << 8) | ((uint)blue << 16);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint windowHandle, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public WindowProcedure WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PaintStruct
    {
        public nint DeviceContext;
        public int Erase;
        public Rect PaintRect;
        public int Restore;
        public int IncUpdate;
        public fixed byte Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint WindowHandle;
        public uint MessageId;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool UnregisterClassW(string className, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint windowHandle, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint windowHandle);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Message message, nint windowHandle, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint windowHandle, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint windowHandle, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint windowHandle, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint windowHandle, nint rect, bool erase);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint windowHandle, out PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint windowHandle, ref PaintStruct paint);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rect rect, nint brush);
    [DllImport("gdi32.dll")] private static extern bool Ellipse(nint dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern nint CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? moduleName);
}
