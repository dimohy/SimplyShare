using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;

namespace SimplyShare.Views;

public sealed class RemoteCursorOverlayWindow : Window
{
    public RemoteCursorOverlayWindow()
    {
        Width = 34;
        Height = 34;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        IsHitTestVisible = false;

        Content = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(3),
            BorderBrush = System.Windows.Media.Brushes.OrangeRed,
            Background = new SolidColorBrush(Color.FromArgb(70, 255, 69, 0)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };

        SourceInitialized += (_, _) => MakeClickThrough();
    }

    public void UpdatePositionToCursor()
    {
        if (!GetCursorPos(out var pt))
            return;

        double xDip = pt.x;
        double yDip = pt.y;

        if (PresentationSource.FromVisual(this) is HwndSource source && source.CompositionTarget is not null)
        {
            var transform = source.CompositionTarget.TransformFromDevice;
            var dip = transform.Transform(new System.Windows.Point(pt.x, pt.y));
            xDip = dip.X;
            yDip = dip.Y;
        }

        Left = xDip - (Width / 2);
        Top = yDip - (Height / 2);
    }

    private void MakeClickThrough()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
            return;

        var hwnd = source.Handle;
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var newStyle = new nint(style.ToInt64() | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        _ = SetWindowLongPtr(hwnd, GWL_EXSTYLE, newStyle);
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
}
