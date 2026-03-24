using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Channels;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplyShare.Core;
using SimplyShare.Core.Input;
using SimplyShare.Core.Transfer;
using SimplyShare.Models;
using SimplyShare.Services;

namespace SimplyShare.ViewModels;

/// <summary>
/// 채팅(DM) 화면 ViewModel — 특정 장치와의 1:1 대화
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly ITransferService _transferService;
    private readonly ISettingsService _settingsService;
    private readonly IClipboardService _clipboardService;
    private ChatConnection? _chatConnection;
    private TaskCompletionSource<bool>? _closeResponseTcs;
    private bool _clipboardSubscribed;
    private readonly RawInputHook _rawInputHook = new();
    private readonly GlobalInputHook _globalInputHook = new();
    private bool _isRemoteInputMode;
    private bool _isBeingRemoteControlled;
    private System.Drawing.Rectangle _boundaryScreenBounds;
    private long _lastInputSendTick;
    private bool _isBoundaryMaster;
    private bool _isPeerInputSharingEnabled;
    private bool _isPeerClipboardSharingEnabled;
    private bool _leftCtrlDown;
    private bool _rightCtrlDown;
    private bool _remoteStopInProgress;
    private long _lastRemoteCursorActivityTick;

    private readonly Channel<InputEvent> _inputEventChannel = Channel.CreateUnbounded<InputEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly CancellationTokenSource _inputSendCts = new();
    private readonly Task _inputSendLoopTask;
    private readonly Channel<InputEvent> _incomingInputChannel = Channel.CreateUnbounded<InputEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
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
    private const double MouseDeltaScale = 1.0;
    private const int RemoteCursorActivityIntervalMs = 16;
    private const int MaxInputBatchSize = 24;
    private const int MaxInjectBatchSize = 48;
    private readonly DispatcherTimer _cursorSuppressTimer;
    private bool _isDisposed;


    private const int RemoteExitThresholdPx = 40;

    internal bool IsRemoteInputModeActive => _isRemoteInputMode;

    /// <summary>
    /// 닫기 시 로컬 상태만 즉시 해제 (커서 복원, 훅 유지)
    /// </summary>
    internal void PrepareForChatWindowClose()
    {
        ExitRemoteMode();
        SetBeingRemoteControlled(false);
    }

    /// <summary>
    /// 상대에게 RemoteControlStop + ChatCloseRequest를 순차 전송한 뒤 로컬 상태 해제.
    /// 타임아웃이 있는 CancellationToken을 전달하여 무한 대기를 방지한다.
    /// 전송 완료(또는 실패) 후에야 로컬 원격 모드를 해제하므로,
    /// 피어가 Stop 신호를 받을 때까지 원격 입력이 정상 동작한다.
    /// </summary>
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
            AppLogger.Log("ChatVM", $"피어 종료 알림 실패: {ex.Message}");
        }
        finally
        {
            // 피어 통보 후 로컬 상태 해제 — 순서가 중요
            ExitRemoteMode();
            SetBeingRemoteControlled(false);
        }
    }

    internal Task SendChatCloseRequestOnlyAsync(CancellationToken cancellationToken = default)
    {
        if (_chatConnection is not { IsConnected: true })
            return Task.CompletedTask;

        var settings = _settingsService.Settings;
        return _chatConnection.SendChatCloseRequestAsync(
            settings.Nickname,
            settings.DeviceId,
            settings.TransferPort,
            cancellationToken);
    }

    public ChatViewModel(
        DeviceInfo targetDevice,
        ITransferService transferService,
        ISettingsService settingsService,
        IClipboardService clipboardService)
    {
        TargetDevice = targetDevice;
        _transferService = transferService;
        _settingsService = settingsService;
        _clipboardService = clipboardService;
        _inputSendLoopTask = Task.Run(() => InputSendLoopAsync(_inputSendCts.Token));
        _inputInjectLoopTask = Task.Run(() => InputInjectLoopAsync(_inputInjectCts.Token));

        _transferService.ProgressChanged += HandleProgressChanged;

        // 페어링 여부 확인
        IsPaired = _settingsService.Settings.PairedDeviceIds.Contains(targetDevice.DeviceId);

        _globalInputHook.KeyDown += vk => OnLocalKey(vk, isDown: true);
        _globalInputHook.KeyUp += vk => OnLocalKey(vk, isDown: false);
        _rawInputHook.MouseDelta += (dx, dy) => OnLocalMouseDelta(dx, dy);
        _globalInputHook.MouseWheel += delta => OnLocalMouseWheel(delta);
        _globalInputHook.MouseDown += button => OnLocalMouseButton(button, isDown: true);
        _globalInputHook.MouseUp += button => OnLocalMouseButton(button, isDown: false);

        _globalInputHook.ShouldBlockInput = () =>
            IsInputSharingEnabled && ((_isRemoteInputMode && _isPeerInputSharingEnabled) || _isBeingRemoteControlled);

        _cursorSuppressTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _cursorSuppressTimer.Tick += (_, _) =>
        {
            if (_isRemoteInputMode)
            {
                NativeCursor.ClearCursorHandle();
            }
        };
    }

    /// <summary>대화 상대 장치</summary>
    public DeviceInfo TargetDevice { get; }

    /// <summary>제목 (대화 상대 닉네임)</summary>
    public string Title => $"{TargetDevice.Nickname} ({TargetDevice.IpAddress})";

    /// <summary>메시지 목록</summary>
    public ObservableCollection<ChatMessage> Messages { get; } = [];

    /// <summary>입력 텍스트</summary>
    [ObservableProperty]
    private string _inputText = string.Empty;

    /// <summary>페어링 여부</summary>
    [ObservableProperty]
    private bool _isPaired;

    /// <summary>전송 중 여부</summary>
    [ObservableProperty]
    private bool _isSending;

    /// <summary>상태 메시지</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>지속 채팅 연결 상태</summary>
    [ObservableProperty]
    private bool _isChatConnected;

    /// <summary>클립보드 공유 On/Off (1:1 연결 필요)</summary>
    [ObservableProperty]
    private bool _isClipboardSharingEnabled;

    /// <summary>마우스/키보드 공유 On/Off (1:1 연결 필요)</summary>
    [ObservableProperty]
    private bool _isInputSharingEnabled;

    /// <summary>입력 공유 상태 표시 (로컬/원격)</summary>
    [ObservableProperty]
    private string _inputModeLabel = string.Empty;

    /// <summary>원격 입력 경계 방향</summary>
    [ObservableProperty]
    private BoundarySide _boundarySide = BoundarySide.Right;

    /// <summary>경계 옵션 편집 가능 여부 (한쪽만 마스터)</summary>
    [ObservableProperty]
    private bool _isBoundarySideEditable;

    /// <summary>상대가 채팅창 종료를 요청함 (UI에서 확인창 띄우기용)</summary>
    public event Action? RemoteCloseRequested;

    /// <summary>원격 제어 상태 변화(피제어 상태 포함) — UI 표시용</summary>
    public event Action<bool>? RemoteControlStateChanged;

    /// <summary>내가 원격 제어 중인지(제어측) 상태 변화 — UI 최적화용</summary>
    public event Action<bool>? RemoteInputModeChanged;

    /// <summary>원격 커서 활동(피제어 측) — 오버레이 갱신용</summary>
    public event Action? RemoteCursorActivity;

    /// <summary>지속 채팅 연결 설정 (양방향 텍스트 통신용)</summary>
    public void SetChatConnection(ChatConnection connection, bool isInitiator)
    {
        // 기존 연결이 있으면 정리
        if (_chatConnection is { IsConnected: true })
        {
            _chatConnection.TextReceived -= OnChatTextReceived;
            _chatConnection.ClipboardTextReceived -= OnClipboardTextReceived;
            _chatConnection.ChatConfigReceived -= OnChatConfigReceived;
            _chatConnection.SharePreferencesReceived -= OnSharePreferencesReceived;
            _chatConnection.RemoteControlStopReceived -= OnRemoteControlStopReceived;
            _chatConnection.ChatCloseRequested -= OnChatCloseRequested;
            _chatConnection.ChatCloseResponded -= OnChatCloseResponded;
            _chatConnection.InputEventReceived -= OnInputEventReceived;
            _chatConnection.Disconnected -= OnChatDisconnected;
            _chatConnection.Dispose();
        }

        _chatConnection = connection;
        _chatConnection.TextReceived += OnChatTextReceived;
        _chatConnection.ClipboardTextReceived += OnClipboardTextReceived;
        _chatConnection.ChatConfigReceived += OnChatConfigReceived;
        _chatConnection.SharePreferencesReceived += OnSharePreferencesReceived;
        _chatConnection.RemoteControlStopReceived += OnRemoteControlStopReceived;
        _chatConnection.ChatCloseRequested += OnChatCloseRequested;
        _chatConnection.ChatCloseResponded += OnChatCloseResponded;
        _chatConnection.InputEventReceived += OnInputEventReceived;
        _chatConnection.Disconnected += OnChatDisconnected;
        IsChatConnected = true;
        StatusMessage = "연결됨";

        // 경계 마스터: 먼저 연결을 건 쪽(initiator)
        _isBoundaryMaster = isInitiator;
        IsBoundarySideEditable = _isBoundaryMaster;

        if (_isBoundaryMaster)
        {
            _ = SendChatConfigAsync(BoundarySide);
        }

        _ = SendSharePreferencesAsync();

        AppLogger.Log("ChatVM", $"ChatConnection 설정 완료 — {TargetDevice.Nickname}");
    }

    private void OnChatConfigReceived(ChatConfig config)
    {
        // 마스터가 아닌 쪽은 반대 방향을 적용
        if (_isBoundaryMaster)
        {
            AppLogger.Log("ChatVM", $"ChatConfig 수신 무시(마스터): {config.BoundarySide}");
            return;
        }

        var mirrored = Mirror(config.BoundarySide);
        _ = App.Current.Dispatcher.InvokeAsync(() =>
        {
            BoundarySide = mirrored;
            IsBoundarySideEditable = false;
        });
    }

    partial void OnBoundarySideChanged(BoundarySide value)
    {
        if (!_isBoundaryMaster)
            return;

        if (!IsChatConnected)
            return;

        _ = SendChatConfigAsync(value);
    }

    private async Task SendChatConfigAsync(BoundarySide side)
    {
        if (_chatConnection is not { IsConnected: true })
            return;

        try
        {
            var settings = _settingsService.Settings;
            var config = new ChatConfig { BoundarySide = side };
            await _chatConnection.SendChatConfigAsync(
                config,
                settings.Nickname,
                settings.DeviceId,
                settings.TransferPort,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLogger.Log("ChatVM", $"ChatConfig 전송 실패: {ex.Message}");
        }
    }

    private static BoundarySide Mirror(BoundarySide side)
        => side switch
        {
            BoundarySide.Right => BoundarySide.Left,
            BoundarySide.Left => BoundarySide.Right,
            BoundarySide.Top => BoundarySide.Bottom,
            BoundarySide.Bottom => BoundarySide.Top,
            _ => BoundarySide.Left
        };

    private bool CanInputShare => IsInputSharingEnabled && _isPeerInputSharingEnabled;
    private bool CanClipboardShare => IsClipboardSharingEnabled && _isPeerClipboardSharingEnabled;

    private void OnSharePreferencesReceived(SharePreferences preferences)
    {
        _isPeerInputSharingEnabled = preferences.InputSharingEnabled;
        _isPeerClipboardSharingEnabled = preferences.ClipboardSharingEnabled;

        if (IsInputSharingEnabled && !_isRemoteInputMode)
        {
            InputModeLabel = _isPeerInputSharingEnabled
                ? "입력 모드: 로컬"
                : "입력 모드: 대기";
        }

        if (!_isPeerInputSharingEnabled && (_isRemoteInputMode || _isBeingRemoteControlled))
        {
            _ = DisableRemoteControlBothSidesAsync();
        }
    }

    private async Task SendSharePreferencesAsync()
    {
        if (_chatConnection is not { IsConnected: true })
            return;

        try
        {
            var settings = _settingsService.Settings;
            var prefs = new SharePreferences
            {
                InputSharingEnabled = IsInputSharingEnabled,
                ClipboardSharingEnabled = IsClipboardSharingEnabled
            };

            await _chatConnection.SendSharePreferencesAsync(
                prefs,
                settings.Nickname,
                settings.DeviceId,
                settings.TransferPort,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLogger.Log("ChatVM", $"SharePreferences 전송 실패: {ex.Message}");
        }
    }

    private void OnChatTextReceived(string senderNickname, string senderDeviceId, string text)
    {
        _ = App.Current.Dispatcher.InvokeAsync(() =>
        {
            var message = new ChatMessage
            {
                Type = ChatMessageType.Text,
                Direction = ChatDirection.Received,
                Text = text
            };
            Messages.Add(message);
        });
    }

    private void OnChatDisconnected()
    {
        _ = App.Current.Dispatcher.InvokeAsync(() =>
        {
            IsChatConnected = false;
            StatusMessage = "연결 끊김";
            _ = DisableRemoteControlBothSidesAsync();
            AppLogger.Log("ChatVM", $"ChatConnection 끊김 — {TargetDevice.Nickname}");
        });
    }

    private void OnRemoteControlStopReceived()
    {
        _ = App.Current.Dispatcher.InvokeAsync(() =>
        {
            _ = DisableRemoteControlBothSidesAsync();
        });
    }

    private void OnChatCloseRequested()
    {
        _ = App.Current.Dispatcher.InvokeAsync(() => RemoteCloseRequested?.Invoke());
    }

    private void OnChatCloseResponded(bool accepted)
    {
        _closeResponseTcs?.TrySetResult(accepted);
        _closeResponseTcs = null;
    }

    private void OnClipboardTextReceived(string senderNickname, string senderDeviceId, string text)
    {
        if (!CanClipboardShare)
            return;

        try
        {
            _clipboardService.SetText(text);
        }
        catch (Exception ex)
        {
            AppLogger.Log("ChatVM", $"클립보드 적용 실패: {ex.Message}");
        }
    }

    private void OnInputEventReceived(InputEvent inputEvent)
    {
        if (!CanInputShare)
            return;

        SetBeingRemoteControlled(true);
        _incomingInputChannel.Writer.TryWrite(inputEvent);
    }

    private void TryTriggerRemoteBoundaryReturn(int dx, int dy)
    {
        if (!_isBeingRemoteControlled)
            return;

        if (_remoteStopInProgress)
            return;

        if (!NativeCursor.TryGetCursorPosition(out var x, out var y))
            return;

        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;

        var shouldStop = BoundarySide switch
        {
            // 요청사항: 오른쪽-왼쪽 조합에서는 왼쪽 끝 좌표 기준 복귀
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

    private void SetBeingRemoteControlled(bool value)
    {
        if (_isBeingRemoteControlled == value)
            return;

        _isBeingRemoteControlled = value;
        // 오버레이는 피제어 상태에서만 표시
        RemoteControlStateChanged?.Invoke(_isBeingRemoteControlled);
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
                AppLogger.Log("ChatVM", $"RemoteControlStop 전송 실패: {ex.Message}");
            }
        }
    }

    partial void OnIsClipboardSharingEnabledChanged(bool value)
    {
        if (value)
        {
            if (_clipboardSubscribed)
                return;
            _clipboardService.ClipboardTextChanged += OnLocalClipboardTextChanged;
            _clipboardSubscribed = true;
        }
        else
        {
            if (!_clipboardSubscribed)
                return;
            _clipboardService.ClipboardTextChanged -= OnLocalClipboardTextChanged;
            _clipboardSubscribed = false;
        }

        _ = SendSharePreferencesAsync();
    }

    partial void OnIsInputSharingEnabledChanged(bool value)
    {
        if (value)
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
                InputModeLabel = _isPeerInputSharingEnabled
                    ? "입력 모드: 로컬"
                    : "입력 모드: 대기";
            }
            catch (Exception ex)
            {
                AppLogger.Log("ChatVM", $"입력 훅 시작 실패: {ex.Message}");
                IsInputSharingEnabled = false;
            }
        }
        else
        {
            // ExitRemoteMode()를 통해 _cursorSuppressTimer도 정상 중지
            ExitRemoteMode();
            _rawInputHook.Stop();
            _globalInputHook.Stop();
            SetBeingRemoteControlled(false);
            CursorClipper.Release();
            CursorVisibility.Show();
            InputModeLabel = string.Empty;
        }

        _ = SendSharePreferencesAsync();

        if (!value)
        {
            _ = DisableRemoteControlBothSidesAsync(notifyPeer: true);
        }
    }

    private static System.Drawing.Rectangle GetBoundaryScreenBounds()
    {
        // 현재 커서가 있는 모니터 기준
        if (!NativeCursor.TryGetCursorPosition(out var x, out var y))
        {
            return System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                   ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        }

        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y));
        return screen.Bounds;
    }

    private void EnterRemoteMode()
    {
        if (_isRemoteInputMode)
            return;

        _isRemoteInputMode = true;
        CursorClipper.ClipToEdge(_boundaryScreenBounds, BoundarySide);
        CursorVisibility.Hide();
        _cursorSuppressTimer.Start();
        InputModeLabel = "입력 모드: 원격";
        RemoteInputModeChanged?.Invoke(true);
    }

    private void ExitRemoteMode()
    {
        if (!_isRemoteInputMode)
            return;

        _isRemoteInputMode = false;
        _cursorSuppressTimer.Stop();
        CursorClipper.Release();
        CursorVisibility.Show();
        InputModeLabel = "입력 모드: 로컬";
        RemoteInputModeChanged?.Invoke(false);
    }

    private void OnLocalKey(int vk, bool isDown)
    {
        if (_isBeingRemoteControlled)
            return;

        // Ctrl 상태 추적
        if (vk == 0xA2) _leftCtrlDown = isDown;   // LCtrl
        if (vk == 0xA3) _rightCtrlDown = isDown;  // RCtrl

        var ctrlDown = _leftCtrlDown || _rightCtrlDown;

        // Ctrl+Esc: 양쪽 원격 제어 즉시 해제
        if (isDown && vk == 0x1B && ctrlDown && (_isRemoteInputMode || _isBeingRemoteControlled))
        {
            _ = DisableRemoteControlBothSidesAsync(notifyPeer: true);
            return;
        }

        if (!IsInputSharingEnabled || !IsChatConnected)
            return;

        // 원격 모드가 아니면 키보드는 로컬 그대로
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

        // 원격 모드 진입: 선택한 경계에 닿아 해당 방향으로 더 이동하려는 경우
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

        // 복귀 판정은 피제어 측(상대)에서 경계 도달 시 RemoteControlStop을 보내도록 한다.

        var now = Environment.TickCount64;
        if (now - _lastInputSendTick < 1)
            return;
        _lastInputSendTick = now;

        if (dx == 0 && dy == 0)
            return;

        var scaledDx = (int)Math.Round(dx * MouseDeltaScale);
        var scaledDy = (int)Math.Round(dy * MouseDeltaScale);

        if (scaledDx == 0 && scaledDy == 0)
            return;

        EnqueueInputEvent(new InputEvent
        {
            Kind = InputEventKind.MouseMove,
            Arg1 = scaledDx,
            Arg2 = scaledDy,
            Arg3 = Environment.TickCount
        });
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

    private int GetAxisDelta(int dx, int dy)
        => BoundarySide switch
        {
            // into-remote direction is positive
            BoundarySide.Right => dx,
            BoundarySide.Left => -dx,
            BoundarySide.Top => -dy,
            BoundarySide.Bottom => dy,
            _ => 0
        };

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

    private void OnLocalClipboardTextChanged(string text)
    {
        if (!CanClipboardShare)
            return;

        if (_chatConnection is not { IsConnected: true })
            return;

        _ = SendClipboardTextAsync(text);
    }

    private async Task SendClipboardTextAsync(string text)
    {
        try
        {
            var settings = _settingsService.Settings;
            await _chatConnection!.SendClipboardTextAsync(
                text,
                settings.Nickname,
                settings.DeviceId,
                settings.TransferPort,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLogger.Log("ChatVM", $"클립보드 전송 실패: {ex.Message}");
        }
    }

    public async Task<bool> RequestRemoteCloseAsync(CancellationToken cancellationToken = default)
    {
        if (_chatConnection is not { IsConnected: true })
            return true;

        if (_closeResponseTcs is not null)
            return false;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _closeResponseTcs = tcs;

        try
        {
            var settings = _settingsService.Settings;
            await _chatConnection.SendChatCloseRequestAsync(
                settings.Nickname,
                settings.DeviceId,
                settings.TransferPort,
                cancellationToken);

            var completed = await Task.WhenAny(
                tcs.Task,
                Task.Delay(10_000, cancellationToken));

            if (completed != tcs.Task)
                return false;

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            AppLogger.Log("ChatVM", $"종료 요청 실패: {ex.Message}");
            return false;
        }
        finally
        {
            _closeResponseTcs = null;
        }
    }

    public async Task CleanupOnWindowClosedAsync()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;

        try
        {
            // 원격 기능 즉시 중단 (가능하면 피어에도 통지)
            await DisableRemoteControlBothSidesAsync(notifyPeer: true);
        }
        catch (Exception ex)
        {
            AppLogger.Log("ChatVM", $"원격 기능 종료 실패: {ex.Message}");
        }

        try
        {
            _cursorSuppressTimer.Stop();
            CursorClipper.Release();
            CursorVisibility.Show();
            _inputSendCts.Cancel();
            _inputInjectCts.Cancel();
            _inputEventChannel.Writer.TryComplete();
            _incomingInputChannel.Writer.TryComplete();

            try
            {
                await _inputSendLoopTask;
                await _inputInjectLoopTask;
            }
            catch (OperationCanceledException)
            {
                // 정상 종료
            }

            _rawInputHook.Stop();
            _globalInputHook.Stop();

            if (_clipboardSubscribed)
            {
                _clipboardService.ClipboardTextChanged -= OnLocalClipboardTextChanged;
                _clipboardSubscribed = false;
            }

            if (_chatConnection is not null)
            {
                _chatConnection.TextReceived -= OnChatTextReceived;
                _chatConnection.ClipboardTextReceived -= OnClipboardTextReceived;
                _chatConnection.ChatConfigReceived -= OnChatConfigReceived;
                _chatConnection.SharePreferencesReceived -= OnSharePreferencesReceived;
                _chatConnection.RemoteControlStopReceived -= OnRemoteControlStopReceived;
                _chatConnection.ChatCloseRequested -= OnChatCloseRequested;
                _chatConnection.ChatCloseResponded -= OnChatCloseResponded;
                _chatConnection.InputEventReceived -= OnInputEventReceived;
                _chatConnection.Disconnected -= OnChatDisconnected;
                _chatConnection.Dispose();
                _chatConnection = null;
            }

            IsInputSharingEnabled = false;
            IsClipboardSharingEnabled = false;
            IsChatConnected = false;
            InputModeLabel = string.Empty;
        }
        catch (Exception ex)
        {
            AppLogger.Log("ChatVM", $"채팅창 종료 정리 실패: {ex.Message}");
        }
    }

    public Task RespondToRemoteCloseAsync(bool accepted, CancellationToken cancellationToken = default)
    {
        if (_chatConnection is not { IsConnected: true })
            return Task.CompletedTask;

        var settings = _settingsService.Settings;
        return _chatConnection.SendChatCloseResponseAsync(
            accepted,
            settings.Nickname,
            settings.DeviceId,
            settings.TransferPort,
            cancellationToken);
    }

    public Task SendInputEventAsync(InputEvent inputEvent, CancellationToken cancellationToken = default)
    {
        if (!IsInputSharingEnabled)
            return Task.CompletedTask;

        if (_chatConnection is not { IsConnected: true })
            return Task.CompletedTask;

        var settings = _settingsService.Settings;
        return _chatConnection.SendInputEventAsync(
            inputEvent,
            settings.Nickname,
            settings.DeviceId,
            settings.TransferPort,
            cancellationToken);
    }

    public Task SendInputEventsAsync(IReadOnlyList<InputEvent> inputEvents, CancellationToken cancellationToken = default)
    {
        if (!IsInputSharingEnabled)
            return Task.CompletedTask;

        if (_chatConnection is not { IsConnected: true })
            return Task.CompletedTask;

        var settings = _settingsService.Settings;
        return _chatConnection.SendInputEventsAsync(
            inputEvents,
            settings.Nickname,
            settings.DeviceId,
            settings.TransferPort,
            cancellationToken);
    }

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

                if (_inputBatchBuffer.Count == 0)
                    continue;

                PrioritizeInputBatchInPlace(_inputBatchBuffer);

                try
                {
                    if (_inputBatchBuffer.Count == 1)
                        await SendInputEventAsync(_inputBatchBuffer[0], cancellationToken);
                    else
                        await SendInputEventsAsync(_inputBatchBuffer, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Log("ChatVM", $"입력 이벤트 전송 실패: {ex.Message}");
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
                if (!CanInputShare)
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

                        var nowTick = Environment.TickCount64;
                        var lastTick = Interlocked.Read(ref _lastRemoteCursorActivityTick);
                        if (nowTick - lastTick >= RemoteCursorActivityIntervalMs)
                        {
                            if (Interlocked.CompareExchange(ref _lastRemoteCursorActivityTick, nowTick, lastTick) == lastTick)
                            {
                                RemoteCursorActivity?.Invoke();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log("ChatVM", $"수신 입력 배치 주입 실패: {ex.Message}");
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
            var inputEvent = batch[i];
            if (inputEvent.Kind is InputEventKind.MouseMove)
                _deferredMouseMoveBatchBuffer.Add(inputEvent);
            else
                _priorityInputBatchBuffer.Add(inputEvent);
        }

        if (_deferredMouseMoveBatchBuffer.Count == 0 || _priorityInputBatchBuffer.Count == 0)
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
                if (_pendingMouseDx == 0 && _pendingMouseDy == 0)
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
                if (_pendingWheelDelta == 0)
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

    /// <summary>텍스트 전송 — ChatConnection 우선, 없으면 per-message TCP</summary>
    [RelayCommand]
    private async Task SendTextAsync(CancellationToken cancellationToken)
    {
        if (InputText is not { Length: > 0 } text)
            return;

        var message = new ChatMessage
        {
            Type = ChatMessageType.Text,
            Direction = ChatDirection.Sent,
            Text = text,
            Status = ChatMessageStatus.Sending
        };

        Messages.Add(message);
        InputText = string.Empty;
        IsSending = true;

        try
        {
            if (_chatConnection is { IsConnected: true })
            {
                // ★ 지속 연결로 전송 (동일 TCP 스트림 재사용)
                await _chatConnection.SendTextAsync(
                    text,
                    _settingsService.Settings.Nickname,
                    _settingsService.Settings.DeviceId,
                    _settingsService.Settings.TransferPort,
                    cancellationToken);
            }
            else
            {
                // 지속 연결 없음 → 기존 per-message 방식 (새 TCP 연결)
                await _transferService.SendTextAsync(TargetDevice, text, cancellationToken);
            }

            message.Status = ChatMessageStatus.Completed;

            // 자동 페어링 (첫 전송 시)
            if (!IsPaired)
            {
                await PairDeviceAsync();
            }
        }
        catch (Exception ex)
        {
            message.Status = ChatMessageStatus.Failed;
            StatusMessage = $"전송 실패: {ex.Message}";
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>파일 전송</summary>
    [RelayCommand]
    private async Task SendFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (paths.Count is 0)
            return;

        foreach (var path in paths)
        {
            var isDir = Directory.Exists(path);
            var name = Path.GetFileName(path);
            var size = isDir ? 0 : new FileInfo(path).Length;

            var message = new ChatMessage
            {
                Type = ChatMessageType.File,
                Direction = ChatDirection.Sent,
                FileName = name,
                FileSize = size,
                FilePath = path,
                Status = ChatMessageStatus.Sending,
                Progress = 0
            };

            Messages.Add(message);
        }

        IsSending = true;
        try
        {
            await _transferService.SendFilesAsync(TargetDevice, paths, cancellationToken);

            // 자동 페어링
            if (!IsPaired)
            {
                await PairDeviceAsync();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"전송 실패: {ex.Message}";
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>수신 메시지 추가 (외부에서 호출)</summary>
    public void AddReceivedMessage(ChatMessage message)
    {
        _ = App.Current.Dispatcher.InvokeAsync(() => Messages.Add(message));
    }

    /// <summary>장치 페어링 (자동 수락 등록)</summary>
    private async Task PairDeviceAsync()
    {
        var settings = _settingsService.Settings;
        if (!settings.PairedDeviceIds.Contains(TargetDevice.DeviceId))
        {
            settings.PairedDeviceIds.Add(TargetDevice.DeviceId);
            await _settingsService.SaveAsync();
            IsPaired = true;
        }
    }

    private void HandleProgressChanged(TransferProgress progress)
    {
        // 현재 대화 상대와 관련된 진행률만 처리
        if (progress.PeerNickname != TargetDevice.Nickname)
            return;

        _ = App.Current.Dispatcher.InvokeAsync(() =>
        {
            StatusMessage = progress.Status switch
            {
                TransferStatus.InProgress => $"{progress.CurrentFileName} 전송 중... {progress.Progress:P0}",
                TransferStatus.Completed => "전송 완료",
                TransferStatus.Failed => "전송 실패",
                _ => string.Empty
            };
        });
    }
}
