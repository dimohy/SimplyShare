using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SimplyShare.Services;

namespace SimplyShare.Core.Clipboard;

[SupportedOSPlatform("windows")]
internal sealed partial class Win32ClipboardService : IClipboardService, IDisposable
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Task? _monitorTask;
    private string? _lastText;

    public event Action<string>? ClipboardTextChanged;

    public string? CurrentText { get; private set; }

    public void Start()
    {
        if (_monitorTask is not null)
        {
            return;
        }

        _monitorTask = MonitorClipboardAsync(_cancellationTokenSource.Token);
    }

    public void Stop()
    {
        if (_cancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
    }

    public void SetText(string text)
    {
        if (text.Length is 0)
        {
            return;
        }

        if (!TrySetText(text))
        {
            AppLogger.Log(nameof(Win32ClipboardService), "클립보드 쓰기 실패");
            return;
        }

        _lastText = text;
        CurrentText = text;
        ClipboardTextChanged?.Invoke(text);
    }

    public void Dispose()
    {
        Stop();

        try
        {
            _monitorTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // 종료 시 예외는 무시한다.
        }

        _cancellationTokenSource.Dispose();
    }

    private async Task MonitorClipboardAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!TryGetText(out var text) || string.Equals(text, _lastText, StringComparison.Ordinal))
                {
                    continue;
                }

                _lastText = text;
                CurrentText = text;
                ClipboardTextChanged?.Invoke(text);
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
    }

    private static bool TryGetText(out string text)
    {
        text = string.Empty;

        if (!TryOpenClipboard())
        {
            return false;
        }

        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var locked = GlobalLock(handle);
            if (locked == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                text = Marshal.PtrToStringUni(locked) ?? string.Empty;
                return true;
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    private static bool TrySetText(string text)
    {
        if (!TryOpenClipboard())
        {
            return false;
        }

        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            var bytes = (text.Length + 1) * sizeof(char);
            var handle = GlobalAlloc(GmemMoveable, (nuint)bytes);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var locked = GlobalLock(handle);
            if (locked == IntPtr.Zero)
            {
                _ = GlobalFree(handle);
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, locked, text.Length);
                Marshal.WriteInt16(locked, text.Length * sizeof(char), 0);
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }

            if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
            {
                _ = GlobalFree(handle);
                return false;
            }

            return true;
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    private static bool TryOpenClipboard()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr newOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr GetClipboardData(uint uFormat);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GlobalFree(IntPtr hMem);
}