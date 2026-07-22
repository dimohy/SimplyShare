using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SimplyShare.Core;

namespace SimplyShare.AppHost;

internal sealed class NativeWindowHook : IDisposable
{
    private const uint WmClose = 0x0010;
    private const uint WmDropFiles = 0x0233;
    private const int SwMinimize = 6;
    private static readonly SubclassProcedure Procedure = WindowProcedure;
    private static long _nextSubclassId;

    private readonly nint _windowHandle;
    private readonly nuint _subclassId;
    private readonly Action<IReadOnlyList<string>> _filesDropped;
    private readonly Action? _closeRequested;
    private GCHandle _selfHandle;
    private int _allowClose;
    private int _closePending;
    private int _disposed;

    public NativeWindowHook(
        nint windowHandle,
        Action<IReadOnlyList<string>> filesDropped,
        Action? closeRequested = null)
    {
        _windowHandle = windowHandle;
        _filesDropped = filesDropped;
        _closeRequested = closeRequested;
        _subclassId = unchecked((nuint)Interlocked.Increment(ref _nextSubclassId));
        _selfHandle = GCHandle.Alloc(this);

        if (!SetWindowSubclass(windowHandle, Procedure, _subclassId, GCHandle.ToIntPtr(_selfHandle)))
        {
            _selfHandle.Free();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "창 메시지 훅을 설치하지 못했습니다.");
        }

        DragAcceptFiles(windowHandle, true);
    }

    public void CloseAfterNotification()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _allowClose, 1);
        _ = PostMessageW(_windowHandle, WmClose, nuint.Zero, nint.Zero);
    }

    public static bool MinimizeIfNeeded(nint windowHandle)
    {
        if (windowHandle == nint.Zero || IsIconic(windowHandle))
        {
            return false;
        }

        _ = ShowWindow(windowHandle, SwMinimize);
        return true;
    }

    public static bool IsMinimized(nint windowHandle)
        => windowHandle != nint.Zero && IsIconic(windowHandle);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DragAcceptFiles(_windowHandle, false);
        _ = RemoveWindowSubclass(_windowHandle, Procedure, _subclassId);
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }

    private static nint WindowProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nint referenceData)
    {
        _ = subclassId;

        if (referenceData != nint.Zero && GCHandle.FromIntPtr(referenceData).Target is NativeWindowHook hook)
        {
            if (message == WmDropFiles)
            {
                hook.HandleDroppedFiles((nint)wParam);
                return nint.Zero;
            }

            if (message == WmClose && hook._closeRequested is not null && Volatile.Read(ref hook._allowClose) == 0)
            {
                if (Interlocked.CompareExchange(ref hook._closePending, 1, 0) == 0)
                {
                    hook._closeRequested();
                }

                return nint.Zero;
            }
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void HandleDroppedFiles(nint dropHandle)
    {
        try
        {
            var fileCount = DragQueryFileW(dropHandle, uint.MaxValue, null, 0);
            var paths = new List<string>((int)fileCount);

            for (uint index = 0; index < fileCount; index++)
            {
                var characterCount = DragQueryFileW(dropHandle, index, null, 0);
                var buffer = new StringBuilder((int)characterCount + 1);
                _ = DragQueryFileW(dropHandle, index, buffer, (uint)buffer.Capacity);
                paths.Add(buffer.ToString());
            }

            if (paths.Count > 0)
            {
                _filesDropped(paths);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log(nameof(NativeWindowHook), $"드롭 파일 처리 실패: {ex}");
        }
        finally
        {
            DragFinish(dropHandle);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint windowHandle,
        SubclassProcedure procedure,
        nuint subclassId,
        nint referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint windowHandle,
        SubclassProcedure procedure,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint windowHandle, uint message, nuint wParam, nint lParam);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(nint windowHandle, [MarshalAs(UnmanagedType.Bool)] bool accept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(nint dropHandle, uint fileIndex, StringBuilder? fileName, uint characterCount);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(nint dropHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint windowHandle, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);
}
