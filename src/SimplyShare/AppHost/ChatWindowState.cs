using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using SimplyShare.Core;
using SimplyShare.Core.Input;
using SimplyShare.Core.Transfer;
using SimplyShare.Models;
using SimplyShare.Services;

namespace SimplyShare.AppHost;

internal sealed class ChatWindowState : IDisposable
{
    private readonly ITransferService _transferService;
    private readonly ISettingsService _settingsService;
    private readonly IClipboardService _clipboardService;
    private readonly Action _requestFrame;
    private readonly Action _requestClose;
    private readonly ConcurrentQueue<Action> _uiActions = new();
    private ChatConnection? _chatConnection;
    private bool _clipboardSubscribed;
    private bool _isConnecting;
    private bool _isDisposed;
    private bool _isBoundaryMaster;
    private bool _isPeerClipboardSharingEnabled;
    private bool _isPeerInputSharingEnabled;

    // ── 리모트 입력 ──
    private readonly RawInputHook _rawInputHook = new();
    private readonly GlobalInputHook _globalInputHook = new();
    private bool _isRemoteInputMode;
    private bool _isBeingRemoteControlled;
    private System.Drawing.Rectangle _boundaryScreenBounds;
    private long _lastInputSendTick;
    private bool _leftCtrlDown;
    private bool _rightCtrlDown;
    private bool _remoteStopInProgress;
    private Timer? _cursorSuppressTimer;

    // ── 입력 채널/배치 ──
    private readonly Channel<InputEvent> _inputEventChannel = Channel.CreateUnbounded<InputEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _inputSendCts = new();
    private readonly Task _inputSendLoopTask;

    private readonly Channel<InputEvent> _incomingInputChannel = Channel.CreateUnbounded<InputEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _inputInjectCts = new();
    private readonly Task _inputInjectLoopTask;

    private readonly object _inputAggregateSync = new();
    private readonly List<InputEvent> _inputBatchBuffer = [];
    private readonly List<InputEvent> _priorityInputBatchBuffer = [];
    private readonly List<InputEvent> _deferredMouseMoveBatchBuffer = [];
    private readonly List<InputEvent> _incomingInjectBatchBuffer = [];
    private int _pendingMouseDx;
    private int _pendingMouseDy;
    private int _pendingMouseTick;
    private bool _mouseMarkerQueued;
    private int _pendingWheelDelta;
    private int _pendingWheelTick;
    private bool _wheelMarkerQueued;

    private const int MaxInputBatchSize = 24;
    private const int MaxInjectBatchSize = 48;

    public ChatWindowState(
        DeviceInfo device,
        ITransferService transferService,
        ISettingsService settingsService,
        IClipboardService clipboardService,
        Action requestFrame,
        Action requestClose,
        IEnumerable<ChatMessage>? initialMessages = null)
    {
        TargetDevice = device;
        _transferService = transferService;
        _settingsService = settingsService;
        _clipboardService = clipboardService;
        _requestFrame = requestFrame;
        _requestClose = requestClose;

        BoundarySide = BoundarySide.Right;
        StatusMessage = "연결 대기 중...";

        if (initialMessages is not null)
        {
            Messages.AddRange(initialMessages);
        }

        _transferService.ProgressChanged += HandleProgressChanged;

        // 입력 전송/주입 루프 시작
        _inputSendLoopTask = Task.Run(() => InputSendLoopAsync(_inputSendCts.Token));
        _inputInjectLoopTask = Task.Run(() => InputInjectLoopAsync(_inputInjectCts.Token));

        // 훅 이벤트 바인딩
        _globalInputHook.KeyDown += vk => OnLocalKey(vk, isDown: true);
        _globalInputHook.KeyUp += vk => OnLocalKey(vk, isDown: false);
        _rawInputHook.MouseDelta += (dx, dy) => OnLocalMouseDelta(dx, dy);
        _globalInputHook.MouseWheel += delta => OnLocalMouseWheel(delta);
        _globalInputHook.MouseDown += button => OnLocalMouseButton(button, isDown: true);
        _globalInputHook.MouseUp += button => OnLocalMouseButton(button, isDown: false);

        _globalInputHook.ShouldBlockInput = () =>
            IsInputSharingEnabled && ((_isRemoteInputMode && _isPeerInputSharingEnabled) || _isBeingRemoteControlled);
    }

    public DeviceInfo TargetDevice { get; }

    public List<ChatMessage> Messages { get; } = [];

    public string InputText = string.Empty;

    public string StatusMessage { get; private set; }

    public bool IsChatConnected { get; private set; }

    public bool IsClipboardSharingEnabled { get; private set; }

    public bool IsInputSharingEnabled { get; private set; }

    public string InputModeLabel { get; private set; } = string.Empty;

    public BoundarySide BoundarySide { get; private set; }

    public bool IsBoundarySideEditable { get; private set; }

    public string Title => $"{TargetDevice.Nickname} ({TargetDevice.IpAddress})";

    public void DrainUiActions()
    {
        while (_uiActions.TryDequeue(out var action))
        {
            action();
        }
    }

    public void EnsureConnected()
    {
        if (_isDisposed || IsChatConnected || _isConnecting)
        {
            return;
        }

        _isConnecting = true;
        StatusMessage = "연결 시도 중...";
        _requestFrame();

        _ = Task.Run(async () =>
        {
            try
            {
                var chatConnection = await _transferService.ConnectChatAsync(TargetDevice);
                EnqueueUi(() => AttachConnectionCore(chatConnection, isInitiator: true, startReceiveLoop: true));
            }
            catch (Exception ex)
            {
                AppLogger.Log(nameof(ChatWindowState), $"채팅 연결 실패: {ex}");
                EnqueueUi(() =>
                {
                    StatusMessage = "연결 대기 중...";
                    _isConnecting = false;
                });
            }
        });
    }

    public void AttachIncomingConnection(ChatConnection chatConnection, bool isInitiator)
        => EnqueueUi(() => AttachConnectionCore(chatConnection, isInitiator, startReceiveLoop: true));

    public void AddReceivedMessage(ChatMessage message)
        => EnqueueUi(() => Messages.Add(message));

    public void SetBoundarySide(BoundarySide side)
    {
        if (BoundarySide == side)
        {
            return;
        }

        BoundarySide = side;
        _requestFrame();

        if (_isBoundaryMaster && IsChatConnected)
        {
            _ = SendChatConfigAsync(side);
        }
    }

    public void SetClipboardSharingEnabled(bool enabled)
    {
        if (IsClipboardSharingEnabled == enabled)
        {
            return;
        }

        IsClipboardSharingEnabled = enabled;

        if (enabled)
        {
            if (!_clipboardSubscribed)
            {
                _clipboardService.ClipboardTextChanged += HandleClipboardTextChanged;
                _clipboardSubscribed = true;
            }
        }
        else if (_clipboardSubscribed)
        {
            _clipboardService.ClipboardTextChanged -= HandleClipboardTextChanged;
            _clipboardSubscribed = false;
        }

        _ = SendSharePreferencesAsync();
        _requestFrame();
    }

    public void SetInputSharingEnabled(bool enabled)
    {
        if (IsInputSharingEnabled == enabled)
        {
            return;
        }

        IsInputSharingEnabled = enabled;

        if (enabled)
        {
            if (!IsChatConnected)
            {
                IsInputSharingEnabled = false;
                return;
            }

            try
            {
                _boundaryScreenBounds = GetBoundaryScreenBounds();
                _isRemoteInputMode = false;
                SetBeingRemoteControlled(false);
                CursorClipper.Release();
                CursorVisibility.Show();

                _rawInputHook.Start();
                _globalInputHook.Start();
            }
            catch (Exception ex)
            {
                AppLogger.Log(nameof(ChatWindowState), $"입력 훅 시작 실패: {ex.Message}");
                IsInputSharingEnabled = false;
            }
        }
        else
        {
            ExitRemoteMode();
            _rawInputHook.Stop();
            _globalInputHook.Stop();
            SetBeingRemoteControlled(false);
            CursorClipper.Release();
            CursorVisibility.Show();

            _ = DisableRemoteControlBothSidesAsync(notifyPeer: true);
        }

        UpdateInputModeLabel();
        _ = SendSharePreferencesAsync();
        _requestFrame();
    }

    public void BeginSendText()
    {
        if (!IsChatConnected || _chatConnection is not { IsConnected: true })
        {
            StatusMessage = "연결이 준비되면 전송할 수 있습니다.";
            _requestFrame();
            return;
        }

        var text = InputText.Trim();
        if (text.Length is 0)
        {
            return;
        }

        var message = new ChatMessage
        {
            Type = ChatMessageType.Text,
            Direction = ChatDirection.Sent,
            Text = text,
            Status = ChatMessageStatus.Sending,
            Progress = 0,
        };

        Messages.Add(message);
        InputText = string.Empty;
        StatusMessage = "텍스트 전송 중...";
        _requestFrame();

        _ = Task.Run(async () =>
        {
            try
            {
                await _chatConnection.SendTextAsync(
                    text,
                    _settingsService.Settings.Nickname,
                    _settingsService.Settings.DeviceId,
                    _settingsService.Settings.TransferPort);

                EnqueueUi(() =>
                {
                    message.Status = ChatMessageStatus.Completed;
                    message.Progress = 1;
                    StatusMessage = "전송 완료";
                });
            }
            catch (Exception ex)
            {
                AppLogger.Log(nameof(ChatWindowState), $"텍스트 전송 실패: {ex}");
                EnqueueUi(() =>
                {
                    message.Status = ChatMessageStatus.Failed;
                    StatusMessage = $"전송 실패: {ex.Message}";
                });
            }
        });
    }

    public void BeginSendFiles(IReadOnlyList<string> paths)
    {
        if (_isDisposed || paths.Count is 0)
        {
            return;
        }

        foreach (var path in paths)
        {
            var isDirectory = Directory.Exists(path);
            Messages.Add(new ChatMessage
            {
                Type = ChatMessageType.File,
                Direction = ChatDirection.Sent,
                FileName = Path.GetFileName(path),
                FileSize = isDirectory || !File.Exists(path) ? 0 : new FileInfo(path).Length,
                FilePath = path,
                Status = ChatMessageStatus.Sending,
                Progress = 0,
            });
        }

        StatusMessage = $"파일 {paths.Count}개 전송 중...";
        _requestFrame();

        _ = Task.Run(async () =>
        {
            try
            {
                await _transferService.SendFilesAsync(TargetDevice, paths);
                EnqueueUi(() => StatusMessage = "파일 전송 완료");
            }
            catch (Exception ex)
            {
                AppLogger.Log(nameof(ChatWindowState), $"파일 전송 실패: {ex}");
                EnqueueUi(() => StatusMessage = $"파일 전송 실패: {ex.Message}");
            }
        });
    }

    public void OpenReceivedFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Log(nameof(ChatWindowState), $"수신 파일 열기 실패: {ex}");
            StatusMessage = $"파일 열기 실패: {ex.Message}";
            _requestFrame();
        }
    }

    public void RequestClose()
    {
        _requestClose();
    }

    private void AttachConnectionCore(ChatConnection chatConnection, bool isInitiator, bool startReceiveLoop)
    {
        _isConnecting = false;

        if (_chatConnection is { IsConnected: true })
        {
            chatConnection.Dispose();
            return;
        }

        _chatConnection = chatConnection;
        _chatConnection.TextReceived += HandleChatTextReceived;
        _chatConnection.ClipboardTextReceived += HandleClipboardTextReceived;
        _chatConnection.ChatConfigReceived += HandleChatConfigReceived;
        _chatConnection.SharePreferencesReceived += HandleSharePreferencesReceived;
        _chatConnection.ChatCloseRequested += HandleChatCloseRequested;
        _chatConnection.Disconnected += HandleChatDisconnected;
        _chatConnection.InputEventReceived += HandleInputEventReceived;
        _chatConnection.RemoteControlStopReceived += HandleRemoteControlStopReceived;

        IsChatConnected = true;
        StatusMessage = "연결됨";
        _isBoundaryMaster = isInitiator;
        IsBoundarySideEditable = isInitiator;
        UpdateInputModeLabel();

        if (startReceiveLoop)
        {
            _chatConnection.StartReceiveLoop();
        }

        if (_isBoundaryMaster)
        {
            _ = SendChatConfigAsync(BoundarySide);
        }

        _ = SendSharePreferencesAsync();
        _requestFrame();
    }

    private void HandleChatTextReceived(string senderNickname, string senderDeviceId, string text)
    {
        _ = senderNickname;
        _ = senderDeviceId;

        EnqueueUi(() =>
        {
            Messages.Add(new ChatMessage
            {
                Type = ChatMessageType.Text,
                Direction = ChatDirection.Received,
                Text = text,
            });
            StatusMessage = "새 메시지 도착";
        });
    }

    private void HandleClipboardTextReceived(string senderNickname, string senderDeviceId, string text)
    {
        _ = senderNickname;
        _ = senderDeviceId;

        if (!IsClipboardSharingEnabled || !_isPeerClipboardSharingEnabled)
        {
            return;
        }

        try
        {
            _clipboardService.SetText(text);
        }
        catch (Exception ex)
        {
            AppLogger.Log(nameof(ChatWindowState), $"클립보드 반영 실패: {ex}");
        }
    }

    private void HandleChatConfigReceived(ChatConfig config)
    {
        if (_isBoundaryMaster)
        {
            return;
        }

        EnqueueUi(() => BoundarySide = Mirror(config.BoundarySide));
    }

    private void HandleSharePreferencesReceived(SharePreferences preferences)
    {
        EnqueueUi(() =>
        {
            _isPeerInputSharingEnabled = preferences.InputSharingEnabled;
            _isPeerClipboardSharingEnabled = preferences.ClipboardSharingEnabled;
            UpdateInputModeLabel();
        });
    }

    private void HandleChatCloseRequested()
    {
        EnqueueUi(() =>
        {
            Messages.Add(new ChatMessage
            {
                Type = ChatMessageType.System,
                Direction = ChatDirection.Received,
                Text = $"{TargetDevice.Nickname}님이 대화를 종료했습니다.",
            });
            _requestClose();
        });
    }

    private void HandleChatDisconnected()
    {
        EnqueueUi(() =>
        {
            // 원격 입력 정리
            ExitRemoteMode();
            SetBeingRemoteControlled(false);
            _rawInputHook.Stop();
            _globalInputHook.Stop();

            IsChatConnected = false;
            IsBoundarySideEditable = false;
            IsInputSharingEnabled = false;
            StatusMessage = "연결 끊김";
            UpdateInputModeLabel();
        });
    }

    private void HandleClipboardTextChanged(string text)
    {
        if (_isDisposed || !IsClipboardSharingEnabled || !_isPeerClipboardSharingEnabled || _chatConnection is not { IsConnected: true })
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _chatConnection.SendClipboardTextAsync(
                    text,
                    _settingsService.Settings.Nickname,
                    _settingsService.Settings.DeviceId,
                    _settingsService.Settings.TransferPort);
            }
            catch (Exception ex)
            {
                AppLogger.Log(nameof(ChatWindowState), $"클립보드 전송 실패: {ex}");
            }
        });
    }

    private async Task SendChatConfigAsync(BoundarySide side)
    {
        if (_chatConnection is not { IsConnected: true })
        {
            return;
        }

        try
        {
            await _chatConnection.SendChatConfigAsync(
                new ChatConfig { BoundarySide = side },
                _settingsService.Settings.Nickname,
                _settingsService.Settings.DeviceId,
                _settingsService.Settings.TransferPort);
        }
        catch (Exception ex)
        {
            AppLogger.Log(nameof(ChatWindowState), $"채팅 설정 전송 실패: {ex}");
        }
    }

    private async Task SendSharePreferencesAsync()
    {
        if (_chatConnection is not { IsConnected: true })
        {
            return;
        }

        try
        {
            await _chatConnection.SendSharePreferencesAsync(
                new SharePreferences
                {
                    ClipboardSharingEnabled = IsClipboardSharingEnabled,
                    InputSharingEnabled = IsInputSharingEnabled,
                },
                _settingsService.Settings.Nickname,
                _settingsService.Settings.DeviceId,
                _settingsService.Settings.TransferPort);
        }
        catch (Exception ex)
        {
            AppLogger.Log(nameof(ChatWindowState), $"공유 설정 전송 실패: {ex}");
        }
    }

    private void HandleProgressChanged(TransferProgress progress)
    {
        if (!string.Equals(progress.PeerNickname, TargetDevice.Nickname, StringComparison.Ordinal))
        {
            return;
        }

        EnqueueUi(() =>
        {
            StatusMessage = progress.Status switch
            {
                TransferStatus.InProgress => $"{progress.CurrentFileName} 전송 중... {progress.Progress:P0}",
                TransferStatus.Completed => "전송 완료",
                TransferStatus.Failed => "전송 실패",
                TransferStatus.Rejected => "전송 거부",
                _ => StatusMessage,
            };

            if (progress.Status is TransferStatus.Completed or TransferStatus.Failed)
            {
                for (var i = Messages.Count - 1; i >= 0; i--)
                {
                    var message = Messages[i];
                    if (message.Type is not ChatMessageType.File || message.Direction is not ChatDirection.Sent || message.Status is not ChatMessageStatus.Sending)
                    {
                        continue;
                    }

                    message.Status = progress.Status is TransferStatus.Completed
                        ? ChatMessageStatus.Completed
                        : ChatMessageStatus.Failed;
                    message.Progress = progress.Progress;
                }
            }
        });
    }

    private void UpdateInputModeLabel()
    {
        InputModeLabel = IsInputSharingEnabled
            ? (_isPeerInputSharingEnabled ? "입력 모드: 로컬" : "입력 모드: 대기")
            : string.Empty;
    }

    private void EnqueueUi(Action action)
    {
        _uiActions.Enqueue(action);
        _requestFrame();
    }

    private static BoundarySide Mirror(BoundarySide side)
        => side switch
        {
            BoundarySide.Right => BoundarySide.Left,
            BoundarySide.Left => BoundarySide.Right,
            BoundarySide.Top => BoundarySide.Bottom,
            BoundarySide.Bottom => BoundarySide.Top,
            _ => BoundarySide.Left,
        };

    // ── 리모트 입력 모드 ──

    private void EnterRemoteMode()
    {
        if (_isRemoteInputMode)
            return;

        _isRemoteInputMode = true;
        CursorClipper.ClipToEdge(_boundaryScreenBounds, BoundarySide);
        CursorVisibility.Hide();
        StartCursorSuppressTimer();
        InputModeLabel = "입력 모드: 원격";
        _requestFrame();
    }

    private void ExitRemoteMode()
    {
        if (!_isRemoteInputMode)
            return;

        _isRemoteInputMode = false;
        StopCursorSuppressTimer();
        CursorClipper.Release();
        CursorVisibility.Show();
        InputModeLabel = "입력 모드: 로컬";
        _requestFrame();
    }

    private void StartCursorSuppressTimer()
    {
        _cursorSuppressTimer?.Dispose();
        _cursorSuppressTimer = new Timer(_ =>
        {
            if (_isRemoteInputMode)
            {
                NativeCursor.ClearCursorHandle();
            }
        }, null, 0, 16);
    }

    private void StopCursorSuppressTimer()
    {
        _cursorSuppressTimer?.Dispose();
        _cursorSuppressTimer = null;
    }

    private void SetBeingRemoteControlled(bool value)
    {
        if (_isBeingRemoteControlled == value)
            return;

        _isBeingRemoteControlled = value;
    }

    private bool ShouldEnterRemoteMode(int dx, int dy, int cursorX, int cursorY)
    {
        return BoundarySide switch
        {
            BoundarySide.Right => dx > 0 && cursorX >= _boundaryScreenBounds.Right - 1 && cursorY >= _boundaryScreenBounds.Top && cursorY < _boundaryScreenBounds.Bottom,
            BoundarySide.Left => dx < 0 && cursorX <= _boundaryScreenBounds.Left && cursorY >= _boundaryScreenBounds.Top && cursorY < _boundaryScreenBounds.Bottom,
            BoundarySide.Top => dy < 0 && cursorY <= _boundaryScreenBounds.Top && cursorX >= _boundaryScreenBounds.Left && cursorX < _boundaryScreenBounds.Right,
            BoundarySide.Bottom => dy > 0 && cursorY >= _boundaryScreenBounds.Bottom - 1 && cursorX >= _boundaryScreenBounds.Left && cursorX < _boundaryScreenBounds.Right,
            _ => false
        };
    }

    private async Task DisableRemoteControlBothSidesAsync(bool notifyPeer = false)
    {
        ExitRemoteMode();
        SetBeingRemoteControlled(false);

        if (notifyPeer && _chatConnection is { IsConnected: true })
        {
            try
            {
                var settings = _settingsService.Settings;
                await _chatConnection.SendRemoteControlStopAsync(
                    settings.Nickname,
                    settings.DeviceId,
                    settings.TransferPort,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppLogger.Log(nameof(ChatWindowState), $"RemoteControlStop 전송 실패: {ex.Message}");
            }
        }
    }

    private void TryTriggerRemoteBoundaryReturn(int dx, int dy)
    {
        if (!_isBeingRemoteControlled)
            return;

        if (_remoteStopInProgress)
            return;

        if (!NativeCursor.TryGetCursorPosition(out var x, out var y))
            return;

        var bounds = ScreenInfo.VirtualScreen;

        var shouldStop = BoundarySide switch
        {
            BoundarySide.Left => dx < 0 && x <= bounds.Left + 1,
            BoundarySide.Right => dx > 0 && x >= bounds.Right - 2,
            BoundarySide.Top => dy < 0 && y <= bounds.Top + 1,
            BoundarySide.Bottom => dy > 0 && y >= bounds.Bottom - 2,
            _ => false
        };

        if (!shouldStop)
            return;

        _remoteStopInProgress = true;
        _ = DisableRemoteControlBothSidesAsync(notifyPeer: true).ContinueWith(_ =>
        {
            _remoteStopInProgress = false;
        }, TaskScheduler.Default);
    }

    private static System.Drawing.Rectangle GetBoundaryScreenBounds()
    {
        if (!NativeCursor.TryGetCursorPosition(out var x, out var y))
        {
            return ScreenInfo.PrimaryMonitorBounds;
        }

        return ScreenInfo.GetMonitorBoundsFromPoint(x, y);
    }

    // ── 로컬 입력 이벤트 핸들러 ──

    private void OnLocalKey(int vk, bool isDown)
    {
        if (_isBeingRemoteControlled)
            return;

        if (vk is 0xA2) _leftCtrlDown = isDown;
        if (vk is 0xA3) _rightCtrlDown = isDown;

        var ctrlDown = _leftCtrlDown || _rightCtrlDown;

        // Ctrl+Esc: 양쪽 원격 제어 즉시 해제
        if (isDown && vk is 0x1B && ctrlDown && (_isRemoteInputMode || _isBeingRemoteControlled))
        {
            _ = DisableRemoteControlBothSidesAsync(notifyPeer: true);
            return;
        }

        if (!IsInputSharingEnabled || !IsChatConnected)
            return;

        if (!_isRemoteInputMode)
            return;

        EnqueueInputEvent(new InputEvent
        {
            Kind = isDown ? InputEventKind.KeyDown : InputEventKind.KeyUp,
            Arg1 = vk,
            Arg3 = Environment.TickCount
        });
    }

    private void OnLocalMouseDelta(int dx, int dy)
    {
        if (_isBeingRemoteControlled)
            return;

        if (!IsInputSharingEnabled || !IsChatConnected)
            return;

        if (!_isRemoteInputMode)
        {
            if (NativeCursor.TryGetCursorPosition(out var x, out var y))
            {
                if (ShouldEnterRemoteMode(dx, dy, x, y))
                {
                    EnterRemoteMode();
                }
            }

            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastInputSendTick < 1)
            return;
        _lastInputSendTick = now;

        if (dx is 0 && dy is 0)
            return;

        EnqueueInputEvent(new InputEvent
        {
            Kind = InputEventKind.MouseMove,
            Arg1 = dx,
            Arg2 = dy,
            Arg3 = Environment.TickCount
        });
    }

    private void OnLocalMouseWheel(int delta)
    {
        if (_isBeingRemoteControlled)
            return;

        if (!IsInputSharingEnabled || !IsChatConnected)
            return;

        if (!_isRemoteInputMode)
            return;

        EnqueueInputEvent(new InputEvent
        {
            Kind = InputEventKind.MouseWheel,
            Arg1 = delta,
            Arg3 = Environment.TickCount
        });
    }

    private void OnLocalMouseButton(int button, bool isDown)
    {
        if (_isBeingRemoteControlled)
            return;

        if (!IsInputSharingEnabled || !IsChatConnected)
            return;

        if (!_isRemoteInputMode)
            return;

        EnqueueInputEvent(new InputEvent
        {
            Kind = isDown ? InputEventKind.MouseDown : InputEventKind.MouseUp,
            Arg1 = button,
            Arg3 = Environment.TickCount
        });
    }

    // ── 입력 큐잉/배치/전송/주입 루프 ──

    private void EnqueueInputEvent(InputEvent inputEvent)
    {
        if (_isDisposed)
            return;

        if (inputEvent.Kind is InputEventKind.MouseMove)
        {
            lock (_inputAggregateSync)
            {
                _pendingMouseDx += inputEvent.Arg1;
                _pendingMouseDy += inputEvent.Arg2;
                _pendingMouseTick = inputEvent.Arg3;

                if (_mouseMarkerQueued)
                    return;

                _mouseMarkerQueued = true;
            }

            _inputEventChannel.Writer.TryWrite(new InputEvent { Kind = InputEventKind.MouseMove });
            return;
        }

        if (inputEvent.Kind is InputEventKind.MouseWheel)
        {
            lock (_inputAggregateSync)
            {
                _pendingWheelDelta += inputEvent.Arg1;
                _pendingWheelTick = inputEvent.Arg3;

                if (_wheelMarkerQueued)
                    return;

                _wheelMarkerQueued = true;
            }

            _inputEventChannel.Writer.TryWrite(new InputEvent { Kind = InputEventKind.MouseWheel });
            return;
        }

        _inputEventChannel.Writer.TryWrite(inputEvent);
    }

    private async Task InputSendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var queuedEvent in _inputEventChannel.Reader.ReadAllAsync(cancellationToken))
            {
                _inputBatchBuffer.Clear();

                if (TryMaterializeInputEvent(queuedEvent, out var firstEvent))
                    _inputBatchBuffer.Add(firstEvent);

                while (_inputBatchBuffer.Count < MaxInputBatchSize && _inputEventChannel.Reader.TryRead(out var nextQueued))
                {
                    if (TryMaterializeInputEvent(nextQueued, out var nextEvent))
                        _inputBatchBuffer.Add(nextEvent);
                }

                if (_inputBatchBuffer.Count is 0)
                    continue;

                PrioritizeInputBatchInPlace(_inputBatchBuffer);

                try
                {
                    var settings = _settingsService.Settings;
                    if (_chatConnection is not { IsConnected: true })
                        continue;

                    if (_inputBatchBuffer.Count is 1)
                    {
                        await _chatConnection.SendInputEventAsync(
                            _inputBatchBuffer[0],
                            settings.Nickname, settings.DeviceId, settings.TransferPort,
                            cancellationToken);
                    }
                    else
                    {
                        await _chatConnection.SendInputEventsAsync(
                            _inputBatchBuffer,
                            settings.Nickname, settings.DeviceId, settings.TransferPort,
                            cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Log(nameof(ChatWindowState), $"입력 이벤트 전송 실패: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
    }

    private async Task InputInjectLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var inputEvent in _incomingInputChannel.Reader.ReadAllAsync(cancellationToken))
            {
                if (!IsInputSharingEnabled || !_isPeerInputSharingEnabled)
                    continue;

                _incomingInjectBatchBuffer.Clear();
                _incomingInjectBatchBuffer.Add(inputEvent);

                while (_incomingInjectBatchBuffer.Count < MaxInjectBatchSize &&
                       _incomingInputChannel.Reader.TryRead(out var nextInputEvent))
                {
                    _incomingInjectBatchBuffer.Add(nextInputEvent);
                }

                PrioritizeInputBatchInPlace(_incomingInjectBatchBuffer);

                try
                {
                    InputInjector.InjectBatch(_incomingInjectBatchBuffer);

                    var moveDx = 0;
                    var moveDy = 0;
                    var hasMouseMove = false;

                    for (var i = 0; i < _incomingInjectBatchBuffer.Count; i++)
                    {
                        var item = _incomingInjectBatchBuffer[i];
                        if (item.Kind is InputEventKind.MouseMove)
                        {
                            hasMouseMove = true;
                            moveDx += item.Arg1;
                            moveDy += item.Arg2;
                        }
                    }

                    if (hasMouseMove)
                    {
                        TryTriggerRemoteBoundaryReturn(moveDx, moveDy);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log(nameof(ChatWindowState), $"수신 입력 배치 주입 실패: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료
        }
    }

    private void PrioritizeInputBatchInPlace(List<InputEvent> batch)
    {
        if (batch.Count <= 1)
            return;

        _priorityInputBatchBuffer.Clear();
        _deferredMouseMoveBatchBuffer.Clear();

        for (var i = 0; i < batch.Count; i++)
        {
            var item = batch[i];
            if (item.Kind is InputEventKind.MouseMove)
                _deferredMouseMoveBatchBuffer.Add(item);
            else
                _priorityInputBatchBuffer.Add(item);
        }

        if (_deferredMouseMoveBatchBuffer.Count is 0 || _priorityInputBatchBuffer.Count is 0)
            return;

        batch.Clear();
        batch.AddRange(_priorityInputBatchBuffer);
        batch.AddRange(_deferredMouseMoveBatchBuffer);
    }

    private bool TryMaterializeInputEvent(InputEvent queuedEvent, out InputEvent eventToSend)
    {
        eventToSend = queuedEvent;

        if (queuedEvent.Kind is InputEventKind.MouseMove)
        {
            lock (_inputAggregateSync)
            {
                if (_pendingMouseDx is 0 && _pendingMouseDy is 0)
                {
                    _mouseMarkerQueued = false;
                    return false;
                }

                eventToSend = new InputEvent
                {
                    Kind = InputEventKind.MouseMove,
                    Arg1 = _pendingMouseDx,
                    Arg2 = _pendingMouseDy,
                    Arg3 = _pendingMouseTick
                };

                _pendingMouseDx = 0;
                _pendingMouseDy = 0;
                _pendingMouseTick = 0;
                _mouseMarkerQueued = false;
            }

            return true;
        }

        if (queuedEvent.Kind is InputEventKind.MouseWheel)
        {
            lock (_inputAggregateSync)
            {
                if (_pendingWheelDelta is 0)
                {
                    _wheelMarkerQueued = false;
                    return false;
                }

                eventToSend = new InputEvent
                {
                    Kind = InputEventKind.MouseWheel,
                    Arg1 = _pendingWheelDelta,
                    Arg3 = _pendingWheelTick
                };

                _pendingWheelDelta = 0;
                _pendingWheelTick = 0;
                _wheelMarkerQueued = false;
            }

            return true;
        }

        return true;
    }

    // ── 수신 이벤트 핸들러 ──

    private void HandleInputEventReceived(InputEvent inputEvent)
    {
        if (!IsInputSharingEnabled || !_isPeerInputSharingEnabled)
            return;

        SetBeingRemoteControlled(true);
        _incomingInputChannel.Writer.TryWrite(inputEvent);
    }

    private void HandleRemoteControlStopReceived()
    {
        EnqueueUi(() => _ = DisableRemoteControlBothSidesAsync());
    }

    /// <summary>피어에게 RemoteControlStop + ChatCloseRequest를 순차 전송 후 로컬 상태 해제</summary>
    internal async Task NotifyPeerAndCloseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_chatConnection is { IsConnected: true })
            {
                var settings = _settingsService.Settings;
                await _chatConnection.SendRemoteControlStopAsync(
                    settings.Nickname, settings.DeviceId, settings.TransferPort, cancellationToken);
                await _chatConnection.SendChatCloseRequestAsync(
                    settings.Nickname, settings.DeviceId, settings.TransferPort, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log(nameof(ChatWindowState), $"피어 종료 알림 실패: {ex.Message}");
        }
        finally
        {
            ExitRemoteMode();
            SetBeingRemoteControlled(false);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        // 원격 입력 정리
        ExitRemoteMode();
        StopCursorSuppressTimer();
        CursorClipper.Release();
        CursorVisibility.Show();

        _inputSendCts.Cancel();
        _inputInjectCts.Cancel();
        _inputEventChannel.Writer.TryComplete();
        _incomingInputChannel.Writer.TryComplete();

        _rawInputHook.Stop();
        _globalInputHook.Stop();
        _rawInputHook.Dispose();
        _globalInputHook.Dispose();

        _transferService.ProgressChanged -= HandleProgressChanged;

        if (_clipboardSubscribed)
        {
            _clipboardService.ClipboardTextChanged -= HandleClipboardTextChanged;
            _clipboardSubscribed = false;
        }

        if (_chatConnection is not null)
        {
            _chatConnection.TextReceived -= HandleChatTextReceived;
            _chatConnection.ClipboardTextReceived -= HandleClipboardTextReceived;
            _chatConnection.ChatConfigReceived -= HandleChatConfigReceived;
            _chatConnection.SharePreferencesReceived -= HandleSharePreferencesReceived;
            _chatConnection.ChatCloseRequested -= HandleChatCloseRequested;
            _chatConnection.Disconnected -= HandleChatDisconnected;
            _chatConnection.InputEventReceived -= HandleInputEventReceived;
            _chatConnection.RemoteControlStopReceived -= HandleRemoteControlStopReceived;
            _chatConnection.Dispose();
            _chatConnection = null;
        }

        IsInputSharingEnabled = false;
        IsClipboardSharingEnabled = false;
        IsChatConnected = false;
        InputModeLabel = string.Empty;
    }
}