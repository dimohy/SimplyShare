using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Duxel.App;
using Duxel.Windows.App;
using SimplyShare.Core;
using SimplyShare.Core.Transfer;
using SimplyShare.Models;
using SimplyShare.Services;

namespace SimplyShare.AppHost;

internal sealed class SimplyShareController
{
    private readonly ISettingsService _settingsService;
    private readonly IDiscoveryService _discoveryService;
    private readonly ITransferService _transferService;
    private readonly IClipboardService _clipboardService;
    private readonly ConcurrentQueue<Action> _uiActions = new();
    private readonly Dictionary<string, ChatSessionState> _chatSessions = [];
    private readonly Dictionary<string, ChatWindowState> _chatWindowStates = [];
    private readonly Dictionary<string, DuxelModelessWindow> _chatWindows = [];
    private readonly HashSet<string> _remoteUiInterferenceDeviceIds = [];
    private readonly HashSet<string> _autoMinimizedChatDeviceIds = [];
    private readonly List<DeviceInfo> _devices = [];
    private readonly SemaphoreSlim _transferPromptLock = new(1, 1);
    private readonly object _remoteCursorOverlaySync = new();
    private NativeRemoteCursorOverlay? _remoteCursorOverlay;
    private nint _mainWindowHandle;
    private int _isInitialized;
    private int _isUpdating;
    private int _isSettingsWindowOpen;
    private int _runtimeStarted;
    private bool _autoMinimizedMainWindow;

    public SimplyShareController(
        ISettingsService settingsService,
        IDiscoveryService discoveryService,
        ITransferService transferService,
        IClipboardService clipboardService)
    {
        _settingsService = settingsService;
        _discoveryService = discoveryService;
        _transferService = transferService;
        _clipboardService = clipboardService;
    }

    public readonly SettingsDraft SettingsDraft = new();
    public readonly SetupDraft SetupDraft = new();

    public string StatusMessage { get; private set; } = "초기화 중...";
    public string LocalIpAddress { get; private set; } = "감지 중...";
    public string? ClipboardText { get; private set; }
    public string? SelectedDeviceId { get; private set; }
    public TransferProgress? ActiveTransferProgress { get; private set; }
    public string? LastError { get; private set; }

    public AppSettings Settings => _settingsService.Settings;
    public byte[] IconData { get; set; } = [];
    public bool IsSetupRequired => !Settings.IsSetupCompleted;
    public IReadOnlyList<DeviceInfo> Devices => _devices;

    public ChatSessionState? SelectedSession
        => SelectedDeviceId is not null && _chatSessions.TryGetValue(SelectedDeviceId, out var session)
            ? session
            : null;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _isInitialized, 1) == 1)
        {
            return;
        }

        await _settingsService.LoadAsync(cancellationToken);
        LoadDraftsFromSettings();
        RefreshLocalIpAddress();

        _discoveryService.DevicesChanged += HandleDevicesChanged;
        _discoveryService.UpdateAvailable += HandleUpdateAvailable;
        _transferService.TransferRequested += HandleTransferRequestedAsync;
        _transferService.ProgressChanged += HandleTransferProgressChanged;
        _transferService.TextReceived += HandleTextReceived;
        _transferService.FilesReceived += HandleFilesReceived;
        _transferService.PeerConnected += HandlePeerConnected;
        _transferService.ChatEstablished += HandleChatEstablished;
        _clipboardService.ClipboardTextChanged += HandleClipboardTextChanged;

        if (!IsSetupRequired)
        {
            await StartRuntimeServicesAsync(cancellationToken);
        }

        var lastUpdateStatus = AutoUpdater.ConsumeLastUpdateStatus();
        if (!string.IsNullOrWhiteSpace(lastUpdateStatus))
        {
            LastError = lastUpdateStatus;
            StatusMessage = lastUpdateStatus;
            return;
        }

        StatusMessage = IsSetupRequired
            ? "초기 설정을 완료하면 검색과 전송을 시작합니다."
            : "같은 네트워크의 장치를 검색 중...";
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _isInitialized, 0, 1) != 1)
        {
            return;
        }

        _discoveryService.DevicesChanged -= HandleDevicesChanged;
        _discoveryService.UpdateAvailable -= HandleUpdateAvailable;
        _transferService.TransferRequested -= HandleTransferRequestedAsync;
        _transferService.ProgressChanged -= HandleTransferProgressChanged;
        _transferService.TextReceived -= HandleTextReceived;
        _transferService.FilesReceived -= HandleFilesReceived;
        _transferService.PeerConnected -= HandlePeerConnected;
        _transferService.ChatEstablished -= HandleChatEstablished;
        _clipboardService.ClipboardTextChanged -= HandleClipboardTextChanged;

        if (Interlocked.Exchange(ref _runtimeStarted, 0) != 0)
        {
            _clipboardService.Stop();
        }

        foreach (var chatWindow in _chatWindows.Values)
        {
            chatWindow.RequestClose();
        }

        foreach (var chatWindowState in _chatWindowStates.Values)
        {
            chatWindowState.Dispose();
        }

        _chatWindows.Clear();
        _chatWindowStates.Clear();
        lock (_remoteCursorOverlaySync)
        {
            _remoteCursorOverlay?.Dispose();
            _remoteCursorOverlay = null;
        }
        if (!IsSetupRequired)
        {
            await _transferService.StopServerAsync(cancellationToken);
            await _discoveryService.StopAsync(cancellationToken);
        }
    }

    public async Task<bool> ShowInitialSetupAsync()
    {
        if (!IsSetupRequired)
        {
            return true;
        }

        var completed = false;
        await DuxelWindowsApp.ShowModalAsync(closeRequested => new DuxelAppOptions
        {
            Window = new DuxelWindowOptions
            {
                Title = "SimplyShare - 초기 설정",
                Width = 400,
                Height = 350,
                MinWidth = 400,
                MinHeight = 350,
                Resizable = false,
                ShowMinimizeButton = false,
                ShowMaximizeButton = false,
                CenterOnScreen = true,
                IconData = IconData,
            },
            Renderer = new DuxelRendererOptions
            {
                Profile = DuxelPerformanceProfile.Display,
                MsaaSamples = 0,
                FontLinearSampling = false,
            },
            Font = new DuxelFontOptions
            {
                FontSize = 14,
                FastStartup = false,
                StartupGlyphs = SimplyShareGlyphCatalog.All,
            },
            Screen = new SimplyShareSetupScreen(SetupDraft, () =>
            {
                var error = TryCompleteInitialSetup();
                completed = error is null;
                return error;
            }, closeRequested),
        });

        return completed;
    }

    private string? TryCompleteInitialSetup()
    {
        try
        {
            LastError = null;
            CompleteSetupAsync().GetAwaiter().GetResult();
            return LastError ?? (IsSetupRequired ? StatusMessage : null);
        }
        catch (Exception ex)
        {
            AppLogger.Log(nameof(SimplyShareController), $"초기 설정 실패: {ex}");
            return $"초기 설정 실패: {ex.Message}";
        }
    }

    private async Task StartRuntimeServicesAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _runtimeStarted, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Settings.DownloadPath);
            await _discoveryService.StartAsync(cancellationToken);
            await _transferService.StartServerAsync(cancellationToken);
            _clipboardService.Start();
            ClipboardText = _clipboardService.CurrentText;
        }
        catch
        {
            Interlocked.Exchange(ref _runtimeStarted, 0);
            throw;
        }
    }

    public void DrainUiActions()
    {
        while (_uiActions.TryDequeue(out var action))
        {
            action();
        }
    }

    public void SelectDevice(string deviceId)
    {
        SelectedDeviceId = deviceId;

        if (_devices.FirstOrDefault(device => device.DeviceId == deviceId) is { } device)
        {
            _ = GetOrCreateSession(device);
        }
    }

    public void ReloadSettingsDraft()
    {
        LoadDraftsFromSettings();
        StatusMessage = "설정 초안이 현재 저장값으로 되돌아갔습니다.";
    }

    public void AttachMainWindowHandle(nint windowHandle)
    {
        Interlocked.Exchange(ref _mainWindowHandle, windowHandle);
    }

    public void RestoreMainWindow()
    {
        var windowHandle = Interlocked.CompareExchange(ref _mainWindowHandle, nint.Zero, nint.Zero);
        if (windowHandle != nint.Zero)
        {
            DuxelWindowsApp.RestoreWindow(windowHandle);
        }
    }

    public void ExitApplication()
    {
        DuxelApp.Exit();
    }

    public async void OpenSettingsWindow()
    {
        if (Interlocked.CompareExchange(ref _isSettingsWindowOpen, 1, 0) != 0)
        {
            StatusMessage = "설정 창이 이미 열려 있습니다.";
            return;
        }

        try
        {
            var dialogDraft = CloneSettingsDraft(SettingsDraft);
            var ownerWindowHandle = Interlocked.CompareExchange(ref _mainWindowHandle, nint.Zero, nint.Zero);

            await DuxelWindowsApp.ShowModalAsync(closeRequested => new DuxelAppOptions
            {
                Window = new DuxelWindowOptions
                {
                    Title = "SimplyShare - 설정",
                    Width = 420,
                    Height = 450,
                    MinWidth = 420,
                    MinHeight = 450,
                    Resizable = false,
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    CenterOnScreen = ownerWindowHandle == nint.Zero,
                    CenterOnOwner = ownerWindowHandle != nint.Zero,
                    OwnerWindowHandle = ownerWindowHandle,
                    IconData = IconData,
                },
                Renderer = new DuxelRendererOptions
                {
                    Profile = DuxelPerformanceProfile.Display,
                    MsaaSamples = 0,
                    FontLinearSampling = false,
                },
                Font = new DuxelFontOptions
                {
                    FontSize = 14,
                    FastStartup = false,
                    StartupGlyphs = SimplyShareGlyphCatalog.All,
                },
                Frame = new DuxelFrameOptions
                {
                    EnableIdleFrameSkip = true,
                    LineHeightScale = 1.1f,
                },
                Screen = new SimplyShareSettingsScreen(dialogDraft, TrySaveSettingsDialog),
            }, ownerWindowHandle);
        }
        catch (Exception ex)
        {
            AppLogger.Log(nameof(SimplyShareController), $"설정 창 열기 실패: {ex}");
            LastError = ex.Message;
            StatusMessage = $"설정 창 열기 실패: {ex.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref _isSettingsWindowOpen, 0);
            DuxelApp.RequestFrame();
        }
    }

    public void OpenChatWindow(string deviceId)
    {
        if (_devices.FirstOrDefault(device => device.DeviceId == deviceId) is not { } device)
        {
            StatusMessage = "채팅을 열 장치를 찾지 못했습니다.";
            return;
        }

        if (_chatWindows.TryGetValue(deviceId, out var existingWindow) && !existingWindow.IsClosed)
        {
            existingWindow.Restore();
            StatusMessage = $"{device.Nickname} 채팅 창을 활성화했습니다.";
            return;
        }

        var sessionState = GetOrCreateSession(device);
        ChatWindowState? chatWindowState = null;
        NativeWindowHook? windowHook = null;
        nint chatWindowHandle = nint.Zero;

        var chatWindow = DuxelWindowsApp.ShowModeless(session => new DuxelAppOptions
        {
            Window = new DuxelWindowOptions
            {
                Title = $"{device.Nickname} ({device.IpAddress})",
                Width = 450,
                Height = 600,
                MinWidth = 450,
                MinHeight = 600,
                Resizable = true,
                ShowMinimizeButton = true,
                ShowMaximizeButton = false,
                CenterOnScreen = true,
                IconData = IconData,
                WindowCreated = windowHandle =>
                {
                    chatWindowHandle = windowHandle;
                    windowHook = new NativeWindowHook(
                        windowHandle,
                        paths => chatWindowState?.BeginSendFiles(paths),
                        closeRequested: () => chatWindowState?.BeginCloseFromWindow(windowHook));
                },
            },
            Renderer = new DuxelRendererOptions
            {
                Profile = DuxelPerformanceProfile.Display,
                MsaaSamples = 0,
                FontLinearSampling = false,
            },
            Font = new DuxelFontOptions
            {
                FontSize = 16,
                FastStartup = true,
                StartupGlyphs = SimplyShareGlyphCatalog.All,
            },
            Frame = new DuxelFrameOptions
            {
                EnableIdleFrameSkip = true,
                LineHeightScale = 1.2f,
            },
            Screen = new SimplyShareChatScreen(
                chatWindowState = new ChatWindowState(
                    device,
                    _transferService,
                    _settingsService,
                    _clipboardService,
                    session.RequestFrame,
                    session.Exit,
                    active => QueueRemoteUiInterferenceChange(deviceId, active),
                    UpdateRemoteCursorOverlayState,
                    UpdateRemoteCursorOverlayPosition,
                    sessionState.Messages),
                text => OpenTextViewerWindow(text, chatWindowHandle)),
        }, () =>
        {
            windowHook?.Dispose();
            if (_chatWindowStates.Remove(deviceId, out var state))
            {
                state.Dispose();
            }

            _chatWindows.Remove(deviceId);
            _remoteUiInterferenceDeviceIds.Remove(deviceId);
            _autoMinimizedChatDeviceIds.Remove(deviceId);
            UpdateMainWindowRemoteInterference();
            DuxelApp.RequestFrame();
        });

        if (chatWindowState is not null)
        {
            _chatWindowStates[deviceId] = chatWindowState;
        }

        _chatWindows[deviceId] = chatWindow;
        StatusMessage = $"{device.Nickname} 채팅 창을 열었습니다.";
    }

    private void UpdateRemoteCursorOverlayState(bool active)
    {
        lock (_remoteCursorOverlaySync)
        {
            if (active)
            {
                _remoteCursorOverlay ??= new NativeRemoteCursorOverlay();
                _remoteCursorOverlay.Show();
            }
            else
            {
                _remoteCursorOverlay?.Hide();
            }
        }
    }

    private void UpdateRemoteCursorOverlayPosition()
    {
        lock (_remoteCursorOverlaySync)
        {
            _remoteCursorOverlay?.UpdatePosition();
        }
    }

    private void QueueRemoteUiInterferenceChange(string deviceId, bool active)
    {
        _uiActions.Enqueue(() => UpdateRemoteUiInterference(deviceId, active));
        DuxelApp.RequestFrame();
    }

    private void UpdateRemoteUiInterference(string deviceId, bool active)
    {
        if (active)
        {
            _remoteUiInterferenceDeviceIds.Add(deviceId);

            if (_chatWindows.TryGetValue(deviceId, out var chatWindow) &&
                NativeWindowHook.MinimizeIfNeeded(chatWindow.WindowHandle))
            {
                _autoMinimizedChatDeviceIds.Add(deviceId);
            }
        }
        else
        {
            _remoteUiInterferenceDeviceIds.Remove(deviceId);

            if (_autoMinimizedChatDeviceIds.Remove(deviceId) &&
                _chatWindows.TryGetValue(deviceId, out var chatWindow) &&
                NativeWindowHook.IsMinimized(chatWindow.WindowHandle))
            {
                chatWindow.Restore();
            }
        }

        UpdateMainWindowRemoteInterference();
    }

    private void UpdateMainWindowRemoteInterference()
    {
        var mainWindowHandle = Interlocked.CompareExchange(ref _mainWindowHandle, nint.Zero, nint.Zero);

        if (_remoteUiInterferenceDeviceIds.Count > 0)
        {
            if (NativeWindowHook.MinimizeIfNeeded(mainWindowHandle))
            {
                _autoMinimizedMainWindow = true;
            }

            return;
        }

        if (_autoMinimizedMainWindow && NativeWindowHook.IsMinimized(mainWindowHandle))
        {
            _autoMinimizedMainWindow = false;
            DuxelWindowsApp.RestoreWindow(mainWindowHandle);
        }
    }

    public void BeginSaveSettings()
        => _ = SaveSettingsAsync();

    public void BeginCompleteSetup()
        => _ = CompleteSetupAsync();

    public void BeginSendSelectedText()
    {
        if (SelectedSession is not { } session)
        {
            StatusMessage = "선택된 장치가 없습니다.";
            return;
        }

        var text = session.DraftText.Trim();
        if (text.Length is 0)
        {
            StatusMessage = "전송할 텍스트를 입력해 주세요.";
            return;
        }

        if (TryGetSelectedDevice(out var device) is false)
        {
            StatusMessage = "선택 장치 정보를 찾지 못했습니다.";
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

        session.Messages.Add(message);
        session.DraftText = string.Empty;
        StatusMessage = $"{device.Nickname}에게 텍스트 전송 중...";
        _ = SendTextCoreAsync(device, text, message);
    }

    public void BeginSendClipboardText()
    {
        var text = ClipboardText?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusMessage = "클립보드에 전송할 텍스트가 없습니다.";
            return;
        }

        if (SelectedSession is null)
        {
            StatusMessage = "클립보드를 보낼 장치를 먼저 선택해 주세요.";
            return;
        }

        SelectedSession.DraftText = text;
        BeginSendSelectedText();
    }

    public void BeginSendSelectedFiles()
    {
        if (SelectedSession is not { } session)
        {
            StatusMessage = "선택된 장치가 없습니다.";
            return;
        }

        var paths = session.DraftPaths
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        BeginSendSelectedFiles(paths);
    }

    public void BeginSendSelectedFiles(IReadOnlyList<string> paths)
    {
        if (SelectedSession is null)
        {
            StatusMessage = "선택된 장치가 없습니다.";
            return;
        }

        if (paths.Count is 0)
        {
            StatusMessage = "보낼 파일 경로를 줄바꿈으로 입력하세요.";
            return;
        }

        if (TryGetSelectedDevice(out var device) is false)
        {
            StatusMessage = "선택 장치 정보를 찾지 못했습니다.";
            return;
        }

        var message = new ChatMessage
        {
            Type = ChatMessageType.System,
            Direction = ChatDirection.Sent,
            Text = $"파일 전송 요청: {paths.Count}개",
            Status = ChatMessageStatus.Sending,
            Progress = 0,
        };

        SelectedSession.Messages.Add(message);
        SelectedSession.DraftPaths = string.Empty;
        StatusMessage = $"{device.Nickname}에게 파일 전송 중...";
        _ = SendFilesCoreAsync(device, paths, message);
    }

    private async Task SaveSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SaveSettingsCoreAsync(SettingsDraft, cancellationToken, reloadDraftsOnSuccess: true);
        }
        catch (Exception ex)
        {
            AppLogger.Log(nameof(SimplyShareController), $"설정 저장 실패: {ex}");
            LastError = ex.Message;
            StatusMessage = $"설정 저장 실패: {ex.Message}";
        }
    }

    private async Task CompleteSetupAsync(CancellationToken cancellationToken = default)
    {
        if (SetupDraft.Nickname.Trim().Length is 0)
        {
            StatusMessage = "닉네임을 입력해 주세요.";
            return;
        }

        try
        {
            var networkRanges = SetupDraft.NetworkRangesText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (networkRanges.FirstOrDefault(static range => !NetworkRangeFilter.IsValidPattern(range)) is { } invalidRange)
            {
                throw new InvalidOperationException($"올바르지 않은 네트워크 대역입니다: {invalidRange}");
            }

            Settings.Nickname = SetupDraft.Nickname.Trim();
            if (networkRanges.Length > 0)
            {
                Settings.NetworkRanges = [.. networkRanges];
            }
            Settings.IsSetupCompleted = true;

            Directory.CreateDirectory(Settings.DownloadPath);
            await _settingsService.SaveAsync(cancellationToken);
            LoadDraftsFromSettings();
            RefreshLocalIpAddress();
            await StartRuntimeServicesAsync(cancellationToken);
            StatusMessage = "초기 설정 완료. 같은 네트워크의 장치를 검색 중...";
        }
        catch (Exception ex)
        {
            AppLogger.Log(nameof(SimplyShareController), $"초기 설정 저장 실패: {ex}");
            LastError = ex.Message;
            StatusMessage = $"초기 설정 저장 실패: {ex.Message}";
        }
    }

    private async Task SendTextCoreAsync(DeviceInfo device, string text, ChatMessage message)
    {
        try
        {
            await _transferService.SendTextAsync(device, text);
            EnqueueUi(() =>
            {
                message.Status = ChatMessageStatus.Completed;
                message.Progress = 1;
                StatusMessage = $"{device.Nickname}에게 텍스트 전송 완료";
            });
        }
        catch (Exception ex)
        {
            AppLogger.Log(nameof(SimplyShareController), $"텍스트 전송 실패: {ex}");
            EnqueueUi(() =>
            {
                message.Status = ChatMessageStatus.Failed;
                LastError = ex.Message;
                StatusMessage = $"텍스트 전송 실패: {ex.Message}";
            });
        }
    }

    private async Task SendFilesCoreAsync(DeviceInfo device, IReadOnlyList<string> paths, ChatMessage message)
    {
        try
        {
            await _transferService.SendFilesAsync(device, paths);
            EnqueueUi(() =>
            {
                message.Status = ChatMessageStatus.Completed;
                message.Progress = 1;

                if (_chatSessions.TryGetValue(device.DeviceId, out var session))
                {
                    foreach (var path in paths)
                    {
                        if (!File.Exists(path))
                        {
                            continue;
                        }

                        session.Messages.Add(new ChatMessage
                        {
                            Type = ChatMessageType.File,
                            Direction = ChatDirection.Sent,
                            FileName = Path.GetFileName(path),
                            FileSize = new FileInfo(path).Length,
                            FilePath = path,
                        });
                    }
                }

                StatusMessage = $"{device.Nickname}에게 파일 전송 완료";
            });
        }
        catch (Exception ex)
        {
            AppLogger.Log(nameof(SimplyShareController), $"파일 전송 실패: {ex}");
            EnqueueUi(() =>
            {
                message.Status = ChatMessageStatus.Failed;
                LastError = ex.Message;
                StatusMessage = $"파일 전송 실패: {ex.Message}";
            });
        }
    }

    private void HandleDevicesChanged(IReadOnlyList<DeviceInfo> devices)
        => EnqueueUi(() =>
        {
            _devices.Clear();
            _devices.AddRange(devices);

            if (_devices.Count is 0)
            {
                SelectedDeviceId = null;
                StatusMessage = "같은 네트워크의 장치를 검색 중...";
            }
            else
            {
                if (SelectedDeviceId is not null && !_devices.Any(device => device.DeviceId == SelectedDeviceId))
                {
                    SelectedDeviceId = null;
                }

                StatusMessage = $"온라인 장치 {_devices.Count}대";
            }

            RefreshLocalIpAddress();
        });

    private void HandleUpdateAvailable(DeviceInfo device)
    {
        if (Interlocked.CompareExchange(ref _isUpdating, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                EnqueueUi(() =>
                {
                    LastError = null;
                    StatusMessage = $"새 버전({device.Version}) 감지: {device.Nickname}에서 업데이트 다운로드 시작";
                    NativeTrayNotification.Show(
                        Interlocked.CompareExchange(ref _mainWindowHandle, nint.Zero, nint.Zero),
                        "SimplyShare 업데이트",
                        StatusMessage);
                });

                var newExePath = await _transferService.RequestUpdateAsync(device);
                if (string.IsNullOrWhiteSpace(newExePath))
                {
                    EnqueueUi(() => StatusMessage = $"업데이트 다운로드 실패 또는 거부됨: {device.Nickname}" );
                    return;
                }

                EnqueueUi(() =>
                {
                    StatusMessage = "업데이트 다운로드 완료: 재시작 준비 중...";
                    NativeTrayNotification.Show(
                        Interlocked.CompareExchange(ref _mainWindowHandle, nint.Zero, nint.Zero),
                        "SimplyShare 업데이트",
                        StatusMessage);
                });
                await Task.Delay(1000).ConfigureAwait(false);

                if (!AutoUpdater.ApplyUpdate(newExePath))
                {
                    EnqueueUi(() =>
                    {
                        LastError = "업데이트 적용에 실패했습니다. 로그를 확인해 주세요.";
                        StatusMessage = LastError;
                    });
                    return;
                }

                EnqueueUi(() => StatusMessage = "업데이트 적용 시작: 앱 종료 중..." );
                await Task.Delay(250).ConfigureAwait(false);

                DuxelApp.Exit();
            }
            catch (Exception ex)
            {
                AppLogger.Log(nameof(SimplyShareController), $"스마트 업데이트 처리 실패: {ex}");
                EnqueueUi(() =>
                {
                    LastError = $"업데이트 처리 중 오류가 발생했습니다: {ex.Message}";
                    StatusMessage = LastError;
                });
            }
            finally
            {
                if (!DuxelApp.IsExitRequested)
                {
                    Interlocked.Exchange(ref _isUpdating, 0);
                }
            }
        });
    }

    private async Task<bool> HandleTransferRequestedAsync(TransferRequest request)
    {
        if (Settings.PairedDeviceIds.Contains(request.SenderDeviceId, StringComparer.Ordinal))
        {
            return true;
        }

        await _transferPromptLock.WaitAsync();
        try
        {
            RestoreMainWindow();
            var ownerWindowHandle = Interlocked.CompareExchange(ref _mainWindowHandle, nint.Zero, nint.Zero);
            NativeTrayNotification.Show(
                ownerWindowHandle,
                $"{request.SenderNickname}님의 파일 전송 요청",
                $"파일 {request.Files.Count}개 ({FormatFileSize(request.TotalSize)})");
            var accepted = false;
            await DuxelWindowsApp.ShowModalAsync(closeRequested => new DuxelAppOptions
            {
                Window = new DuxelWindowOptions
                {
                    Title = "전송 요청",
                    Width = 390,
                    Height = 230,
                    MinWidth = 350,
                    MinHeight = 230,
                    Resizable = false,
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    CenterOnScreen = ownerWindowHandle == nint.Zero,
                    CenterOnOwner = ownerWindowHandle != nint.Zero,
                    OwnerWindowHandle = ownerWindowHandle,
                    IconData = IconData,
                },
                Renderer = new DuxelRendererOptions { Profile = DuxelPerformanceProfile.Display },
                Font = new DuxelFontOptions
                {
                    FontSize = 14,
                    FastStartup = false,
                    StartupGlyphs = SimplyShareGlyphCatalog.All,
                },
                Screen = new SimplyShareTransferRequestScreen(request, value =>
                {
                    accepted = value;
                    closeRequested();
                }),
            }, ownerWindowHandle);

            if (accepted && !Settings.PairedDeviceIds.Contains(request.SenderDeviceId, StringComparer.Ordinal))
            {
                Settings.PairedDeviceIds.Add(request.SenderDeviceId);
                await _settingsService.SaveAsync();
            }

            EnqueueUi(() => StatusMessage = accepted
                ? $"{request.SenderNickname}의 전송 요청을 수락했습니다."
                : $"{request.SenderNickname}의 전송 요청을 거부했습니다.");
            return accepted;
        }
        finally
        {
            _transferPromptLock.Release();
        }
    }

    private void HandleTransferProgressChanged(TransferProgress progress)
        => EnqueueUi(() =>
        {
            ActiveTransferProgress = progress;
            StatusMessage = progress.Status switch
            {
                TransferStatus.Completed => $"{progress.PeerNickname} 전송 완료",
                TransferStatus.Failed => $"{progress.PeerNickname} 전송 실패",
                TransferStatus.Rejected => $"{progress.PeerNickname} 전송 거부",
                _ => $"{progress.PeerNickname} 전송 {progress.Progress:P0}",
            };
        });

    private void HandleTextReceived(string senderNickname, string senderDeviceId, string text)
        => EnqueueUi(() =>
        {
            var device = FindDevice(senderDeviceId, senderNickname);
            var session = GetOrCreateSession(device);
            var message = new ChatMessage
            {
                Type = ChatMessageType.Text,
                Direction = ChatDirection.Received,
                Text = text,
            };

            session.Messages.Add(message);

            if (_chatWindowStates.TryGetValue(device.DeviceId, out var chatWindowState))
            {
                chatWindowState.AddReceivedMessage(message);
            }
            else
            {
                OpenChatWindow(device.DeviceId);
            }

            ClipboardText = text;
            _clipboardService.SetText(text);
            StatusMessage = $"{senderNickname}에게서 텍스트를 받았습니다.";
        });

    private void HandleFilesReceived(string senderNickname, string senderDeviceId, IReadOnlyList<string> filePaths)
        => EnqueueUi(() =>
        {
            var device = FindDevice(senderDeviceId, senderNickname);
            var session = GetOrCreateSession(device);
            var receivedMessages = new List<ChatMessage>(filePaths.Count);

            foreach (var filePath in filePaths)
            {
                var message = new ChatMessage
                {
                    Type = ChatMessageType.File,
                    Direction = ChatDirection.Received,
                    FileName = Path.GetFileName(filePath),
                    FileSize = File.Exists(filePath) ? new FileInfo(filePath).Length : 0,
                    FilePath = filePath,
                };

                session.Messages.Add(message);
                receivedMessages.Add(message);
            }

            if (_chatWindowStates.TryGetValue(device.DeviceId, out var chatWindowState))
            {
                foreach (var message in receivedMessages)
                {
                    chatWindowState.AddReceivedMessage(message);
                }
            }
            else
            {
                OpenChatWindow(device.DeviceId);
            }

            StatusMessage = $"{senderNickname}에게서 파일 {filePaths.Count}개를 받았습니다.";
            NativeTrayNotification.Show(
                Interlocked.CompareExchange(ref _mainWindowHandle, nint.Zero, nint.Zero),
                "SimplyShare",
                $"{senderNickname}님과의 수신이 완료되었습니다.");
        });

    private void HandlePeerConnected(DeviceInfo device)
        => _discoveryService.AddOrUpdateDevice(device);

    private void HandleChatEstablished(ChatConnection chatConnection)
        => EnqueueUi(() =>
        {
            var device = FindDevice(chatConnection.PeerDeviceId, chatConnection.PeerNickname);
            OpenChatWindow(device.DeviceId);

            if (_chatWindowStates.TryGetValue(device.DeviceId, out var chatWindowState))
            {
                chatWindowState.AttachIncomingConnection(chatConnection, isInitiator: false);
            }
            else
            {
                chatConnection.Dispose();
            }
        });

    private void HandleClipboardTextChanged(string text)
        => EnqueueUi(() => ClipboardText = text);

    private void LoadDraftsFromSettings()
    {
        SettingsDraft.Nickname = Settings.Nickname;
        SettingsDraft.DownloadPath = Settings.DownloadPath;
        SettingsDraft.RunAtStartup = Settings.RunAtStartup;
        SettingsDraft.DiscoveryPort = Settings.DiscoveryPort;
        SettingsDraft.TransferPort = Settings.TransferPort;
        SettingsDraft.NetworkRangesText = string.Join(Environment.NewLine, Settings.NetworkRanges);

        SetupDraft.Nickname = Settings.Nickname;
        SetupDraft.DownloadPath = Settings.DownloadPath;
        SetupDraft.NetworkRangesText = string.Join(Environment.NewLine, Settings.NetworkRanges);
    }

    private void RefreshLocalIpAddress()
    {
        LocalIpAddress = TryGetLocalIpv4(Settings.NetworkRanges) ?? "감지 실패";
    }

    private ChatSessionState GetOrCreateSession(DeviceInfo device)
    {
        if (_chatSessions.TryGetValue(device.DeviceId, out var session))
        {
            session.DeviceNickname = device.Nickname;
            return session;
        }

        session = new ChatSessionState
        {
            DeviceId = device.DeviceId,
            DeviceNickname = device.Nickname,
        };
        _chatSessions[device.DeviceId] = session;
        return session;
    }

    private DeviceInfo FindDevice(string senderDeviceId, string senderNickname)
    {
        if (_devices.FirstOrDefault(device => device.DeviceId == senderDeviceId) is { } knownDevice)
        {
            return knownDevice;
        }

        var inferredDevice = new DeviceInfo
        {
            DeviceId = senderDeviceId,
            Nickname = senderNickname,
            IpAddress = "unknown",
            Port = Settings.TransferPort,
            IsOnline = true,
        };

        _devices.Add(inferredDevice);
        return inferredDevice;
    }

    private bool TryGetSelectedDevice(out DeviceInfo device)
    {
        if (SelectedDeviceId is not null && _devices.FirstOrDefault(candidate => candidate.DeviceId == SelectedDeviceId) is { } selected)
        {
            device = selected;
            return true;
        }

        device = null!;
        return false;
    }

    private static string NormalizePath(string path)
        => string.IsNullOrWhiteSpace(path)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SimplyShare")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024d && unit < units.Length - 1)
        {
            size /= 1024d;
            unit++;
        }

        return $"{size:F1} {units[unit]}";
    }

    private static SettingsDraft CloneSettingsDraft(SettingsDraft source)
        => new()
        {
            Nickname = source.Nickname,
            DownloadPath = source.DownloadPath,
            RunAtStartup = source.RunAtStartup,
            DiscoveryPort = source.DiscoveryPort,
            TransferPort = source.TransferPort,
            NetworkRangesText = source.NetworkRangesText,
        };

    private string? TrySaveSettingsDialog(SettingsDraft dialogDraft)
    {
        try
        {
            SaveSettingsCoreAsync(dialogDraft, CancellationToken.None, reloadDraftsOnSuccess: true).GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Log(nameof(SimplyShareController), $"설정 대화상자 저장 실패: {ex}");
            EnqueueUi(() =>
            {
                LastError = ex.Message;
                StatusMessage = $"설정 저장 실패: {ex.Message}";
            });

            return $"설정 저장 실패: {ex.Message}";
        }
    }

    private async Task SaveSettingsCoreAsync(SettingsDraft sourceDraft, CancellationToken cancellationToken, bool reloadDraftsOnSuccess)
    {
        var nickname = sourceDraft.Nickname.Trim();
        if (nickname.Length is 0)
        {
            throw new InvalidOperationException("닉네임을 입력해 주세요.");
        }

        if (sourceDraft.DiscoveryPort is < 1 or > 65_535 || sourceDraft.TransferPort is < 1 or > 65_535)
        {
            throw new InvalidOperationException("포트는 1부터 65535 사이의 값이어야 합니다.");
        }

        var networkRanges = sourceDraft.NetworkRangesText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (networkRanges.FirstOrDefault(static range => !NetworkRangeFilter.IsValidPattern(range)) is { } invalidRange)
        {
            throw new InvalidOperationException($"올바르지 않은 네트워크 대역입니다: {invalidRange}");
        }

        Settings.Nickname = nickname;
        Settings.DownloadPath = NormalizePath(sourceDraft.DownloadPath);
        Settings.RunAtStartup = sourceDraft.RunAtStartup;
        Settings.DiscoveryPort = sourceDraft.DiscoveryPort;
        Settings.TransferPort = sourceDraft.TransferPort;
        Settings.NetworkRanges = [.. networkRanges];

        Directory.CreateDirectory(Settings.DownloadPath);
        await _settingsService.SaveAsync(cancellationToken);

        EnqueueUi(() =>
        {
            if (reloadDraftsOnSuccess)
            {
                LoadDraftsFromSettings();
            }

            RefreshLocalIpAddress();
            LastError = null;
            StatusMessage = "설정 저장 완료. 포트 변경은 재시작 시 반영됩니다.";
        });
    }

    private void EnqueueUi(Action action)
    {
        _uiActions.Enqueue(action);
        DuxelApp.RequestFrame();
    }

    private static string? TryGetLocalIpv4(IReadOnlyList<string> networkRanges)
    {
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus is not OperationalStatus.Up)
            {
                continue;
            }

            if (iface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var addr in iface.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily is not AddressFamily.InterNetwork)
                {
                    continue;
                }

                var ip = addr.Address.ToString();
                if (NetworkRangeFilter.IsInRange(ip, networkRanges))
                {
                    return ip;
                }
            }
        }

        return null;
    }

    private async void OpenTextViewerWindow(string text, nint ownerWindowHandle)
    {
        try
        {
            await DuxelWindowsApp.ShowModalAsync(closeRequested => new DuxelAppOptions
            {
                Window = new DuxelWindowOptions
                {
                    Title = "텍스트 보기",
                    Width = 500,
                    Height = 400,
                    MinWidth = 500,
                    MinHeight = 400,
                    Resizable = true,
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    CenterOnScreen = ownerWindowHandle == nint.Zero,
                    CenterOnOwner = ownerWindowHandle != nint.Zero,
                    OwnerWindowHandle = ownerWindowHandle,
                    IconData = IconData,
                },
                Renderer = new DuxelRendererOptions
                {
                    Profile = DuxelPerformanceProfile.Display,
                    MsaaSamples = 0,
                    FontLinearSampling = false,
                },
                Font = new DuxelFontOptions
                {
                    FontSize = 14,
                    FastStartup = false,
                    StartupGlyphs = SimplyShareGlyphCatalog.All,
                },
                Frame = new DuxelFrameOptions
                {
                    EnableIdleFrameSkip = true,
                    LineHeightScale = 1.1f,
                },
                Screen = new SimplyShareTextViewerScreen(text, closeRequested),
            }, ownerWindowHandle);
        }
        catch (Exception ex)
        {
            AppLogger.Log(nameof(SimplyShareController), $"텍스트 보기 창 열기 실패: {ex}");
            EnqueueUi(() => StatusMessage = $"텍스트 보기 실패: {ex.Message}");
        }
    }
}
internal sealed class ChatSessionState
{
    public string DeviceId = string.Empty;
    public string DeviceNickname = string.Empty;
    public string DraftText = string.Empty;
    public string DraftPaths = string.Empty;
    public readonly List<ChatMessage> Messages = [];
}

internal sealed class SettingsDraft
{
    public string Nickname = string.Empty;
    public string DownloadPath = string.Empty;
    public bool RunAtStartup;
    public int DiscoveryPort = NetworkDefaults.DiscoveryPort;
    public int TransferPort = NetworkDefaults.TransferPort;
    public string NetworkRangesText = string.Empty;
}

internal sealed class SetupDraft
{
    public string Nickname = string.Empty;
    public string DownloadPath = string.Empty;
    public string NetworkRangesText = string.Empty;
}
