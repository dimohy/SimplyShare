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
    private readonly List<DeviceInfo> _devices = [];
    private readonly List<PendingTransferPrompt> _pendingTransferPrompts = [];
    private nint _mainWindowHandle;
    private int _isInitialized;
    private int _isUpdating;
    private int _isSettingsWindowOpen;

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
    public PendingTransferPrompt? ActiveTransferPrompt => _pendingTransferPrompts.Count is 0 ? null : _pendingTransferPrompts[0];

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

        Directory.CreateDirectory(Settings.DownloadPath);
        await _discoveryService.StartAsync(cancellationToken);
        await _transferService.StartServerAsync(cancellationToken);
        _clipboardService.Start();
        ClipboardText = _clipboardService.CurrentText;

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

        _clipboardService.Stop();

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

        await _transferService.StopServerAsync(cancellationToken);
        await _discoveryService.StopAsync(cancellationToken);
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
            StatusMessage = $"{device.Nickname} 장치를 선택했습니다.";
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
                Theme = SimplyShareTheme.Light,
                Window = new DuxelWindowOptions
                {
                    Title = "SimplyShare - 설정",
                    Width = 390,
                    Height = 510,
                    MinWidth = 390,
                    MinHeight = 510,
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
                Screen = new SimplyShareSettingsScreen(dialogDraft, TrySaveSettingsDialog, closeRequested),
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

        var chatWindow = DuxelWindowsApp.ShowModeless(session => new DuxelAppOptions
        {
            Theme = SimplyShareTheme.Light,
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
            Screen = new SimplyShareChatScreen(chatWindowState = new ChatWindowState(
                device,
                _transferService,
                _settingsService,
                _clipboardService,
                session.RequestFrame,
                session.Exit,
                sessionState.Messages)),
        }, () =>
        {
            if (_chatWindowStates.Remove(deviceId, out var state))
            {
                state.Dispose();
            }

            _chatWindows.Remove(deviceId);
            DuxelApp.RequestFrame();
        });

        if (chatWindowState is not null)
        {
            _chatWindowStates[deviceId] = chatWindowState;
        }

        _chatWindows[deviceId] = chatWindow;
        StatusMessage = $"{device.Nickname} 채팅 창을 열었습니다.";
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

        if (paths.Length is 0)
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
            Text = $"파일 전송 요청: {paths.Length}개",
            Status = ChatMessageStatus.Sending,
            Progress = 0,
        };

        session.Messages.Add(message);
        session.DraftPaths = string.Empty;
        StatusMessage = $"{device.Nickname}에게 파일 전송 중...";
        _ = SendFilesCoreAsync(device, paths, message);
    }

    public void AcceptActiveTransfer()
    {
        if (ActiveTransferPrompt is not { } prompt)
        {
            return;
        }

        _pendingTransferPrompts.RemoveAt(0);

        if (!Settings.PairedDeviceIds.Contains(prompt.Request.SenderDeviceId, StringComparer.Ordinal))
        {
            Settings.PairedDeviceIds.Add(prompt.Request.SenderDeviceId);
            _ = _settingsService.SaveAsync();
        }

        prompt.Resolve(true);
        StatusMessage = $"{prompt.Request.SenderNickname}의 전송 요청을 수락했습니다.";
    }

    public void RejectActiveTransfer()
    {
        if (ActiveTransferPrompt is not { } prompt)
        {
            return;
        }

        _pendingTransferPrompts.RemoveAt(0);
        prompt.Resolve(false);
        StatusMessage = $"{prompt.Request.SenderNickname}의 전송 요청을 거부했습니다.";
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
            Settings.Nickname = SetupDraft.Nickname.Trim();
            Settings.DownloadPath = NormalizePath(SetupDraft.DownloadPath);
            Settings.NetworkRanges =
            [
                .. SetupDraft.NetworkRangesText
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ];
            Settings.IsSetupCompleted = true;

            Directory.CreateDirectory(Settings.DownloadPath);
            await _settingsService.SaveAsync(cancellationToken);
            LoadDraftsFromSettings();
            RefreshLocalIpAddress();
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
            _devices.AddRange(devices.OrderByDescending(static device => device.IsOnline).ThenBy(static device => device.Nickname, StringComparer.OrdinalIgnoreCase));

            if (_devices.Count is 0)
            {
                SelectedDeviceId = null;
                StatusMessage = "같은 네트워크의 장치를 찾지 못했습니다.";
            }
            else
            {
                if (SelectedDeviceId is null || !_devices.Any(device => device.DeviceId == SelectedDeviceId))
                {
                    SelectDevice(_devices[0].DeviceId);
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
                });

                var newExePath = await _transferService.RequestUpdateAsync(device);
                if (string.IsNullOrWhiteSpace(newExePath))
                {
                    EnqueueUi(() => StatusMessage = $"업데이트 다운로드 실패 또는 거부됨: {device.Nickname}" );
                    return;
                }

                EnqueueUi(() => StatusMessage = "업데이트 다운로드 완료: 재시작 준비 중..." );
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

    private Task<bool> HandleTransferRequestedAsync(TransferRequest request)
    {
        if (Settings.PairedDeviceIds.Contains(request.SenderDeviceId, StringComparer.Ordinal))
        {
            return Task.FromResult(true);
        }

        var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        EnqueueUi(() =>
        {
            _pendingTransferPrompts.Add(new PendingTransferPrompt(request, completionSource));
            StatusMessage = $"{request.SenderNickname} 장치의 전송 요청 도착";
        });

        return completionSource.Task;
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
            SelectedDeviceId ??= device.DeviceId;
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

            SelectedDeviceId ??= device.DeviceId;
            StatusMessage = $"{senderNickname}에게서 파일 {filePaths.Count}개를 받았습니다.";
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
        Settings.Nickname = sourceDraft.Nickname.Trim();
        Settings.DownloadPath = NormalizePath(sourceDraft.DownloadPath);
        Settings.RunAtStartup = sourceDraft.RunAtStartup;
        Settings.DiscoveryPort = Math.Clamp(sourceDraft.DiscoveryPort, 1, 65_535);
        Settings.TransferPort = Math.Clamp(sourceDraft.TransferPort, 1, 65_535);
        Settings.NetworkRanges =
        [
            .. sourceDraft.NetworkRangesText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ];

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
        if (networkRanges.Count is 0)
        {
            return null;
        }

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

internal sealed class PendingTransferPrompt
{
    private readonly TaskCompletionSource<bool> _completionSource;

    public PendingTransferPrompt(TransferRequest request, TaskCompletionSource<bool> completionSource)
    {
        Request = request;
        _completionSource = completionSource;
    }

    public TransferRequest Request { get; }

    public void Resolve(bool accepted)
        => _completionSource.TrySetResult(accepted);
}