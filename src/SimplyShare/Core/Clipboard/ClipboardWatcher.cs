using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SimplyShare.Core.Clipboard;

/// <summary>
/// Windows 클립보드 변경 감시 서비스 (AddClipboardFormatListener API)
/// </summary>
public sealed class ClipboardWatcher : Services.IClipboardService, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private string? _lastText;

    public event Action<string>? ClipboardTextChanged;
    public string? CurrentText => _lastText;

    public void Start()
    {
        // WPF 메인 윈도우의 핸들이 필요 — 숨겨진 메시지 전용 윈도우 생성
        var parameters = new HwndSourceParameters("SimplyShareClipboard")
        {
            Width = 0,
            Height = 0,
            PositionX = -100,
            PositionY = -100,
            WindowStyle = 0 // 안 보이는 윈도우
        };

        _hwndSource = new HwndSource(parameters);
        _hwnd = _hwndSource.Handle;
        _hwndSource.AddHook(WndProc);

        AddClipboardFormatListener(_hwnd);
    }

    public void Stop()
    {
        if (_hwnd != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource?.Dispose();
        _hwndSource = null;
    }

    public void SetText(string text)
    {
        // 자신이 설정한 변경은 무시하기 위해 마지막 텍스트 기록
        _lastText = text;
        System.Windows.Clipboard.SetText(text);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_CLIPBOARDUPDATE)
        {
            handled = true;
            OnClipboardChanged();
        }

        return IntPtr.Zero;
    }

    private void OnClipboardChanged()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
                return;

            var text = System.Windows.Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text == _lastText)
                return;

            _lastText = text;
            ClipboardTextChanged?.Invoke(text);
        }
        catch (COMException)
        {
            // 클립보드 접근 실패 — 다른 앱이 사용 중일 수 있음
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
