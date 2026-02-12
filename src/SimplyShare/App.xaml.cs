using System.IO;
using System.Windows;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using SimplyShare.Core.Clipboard;
using SimplyShare.Core.Discovery;
using SimplyShare.Models;
using SimplyShare.Services;
using SimplyShare.ViewModels;
using SimplyShare.Views;

namespace SimplyShare;

/// <summary>
/// 애플리케이션 엔트리포인트
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    /// <summary>업데이트/시스템 종료 등으로 강제 종료 중인지 여부</summary>
    public static bool IsForcedShutdown { get; private set; }

    /// <summary>서비스 프로바이더 (DI 컨테이너)</summary>
    public IServiceProvider Services => _serviceProvider ?? throw new InvalidOperationException("Services not initialized");

    /// <summary>현재 App 인스턴스</summary>
    public new static App Current => (App)Application.Current;

    /// <summary>강제 종료 플래그를 세우고 앱 종료</summary>
    public static void BeginForcedShutdown()
    {
        IsForcedShutdown = true;
        if (Application.Current is App app)
        {
            app.Shutdown();
        }
    }

    private static void ShowUpdatePopup(string message, MessageBoxImage image = MessageBoxImage.Information)
    {
        if (Application.Current is not App app)
            return;

        app.Dispatcher.Invoke(() =>
        {
            var owner = app.MainWindow;
            if (owner is not null && owner.IsVisible)
            {
                MessageBox.Show(owner, message, "SimplyShare 업데이트", MessageBoxButton.OK, image);
            }
            else
            {
                MessageBox.Show(message, "SimplyShare 업데이트", MessageBoxButton.OK, image);
            }
        });
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            // 설정 로드
            var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
            await settingsService.LoadAsync();

            // 최초 설정 미완료 시 설정 화면
            if (!settingsService.Settings.IsSetupCompleted)
            {
                var setupVm = _serviceProvider.GetRequiredService<SetupViewModel>();
                var setupWindow = new SetupWindow(setupVm);
                var result = setupWindow.ShowDialog();

                if (result is not true)
                {
                    Shutdown();
                    return;
                }
            }

            // 수신 파일 저장 폴더 생성
            Directory.CreateDirectory(settingsService.Settings.DownloadPath);

            // 서비스 시작
            var discoveryService = _serviceProvider.GetRequiredService<IDiscoveryService>();
            var transferService = _serviceProvider.GetRequiredService<ITransferService>();
            var clipboardService = _serviceProvider.GetRequiredService<IClipboardService>();

            // 전송 요청 수신 시 — 페어링된 장치는 자동 수락, 아니면 다이얼로그
            transferService.TransferRequested += async request =>
            {
                var isPaired = settingsService.Settings.PairedDeviceIds.Contains(request.SenderDeviceId);
                if (isPaired)
                    return true;

                var accepted = false;
                await Dispatcher.InvokeAsync(() =>
                {
                    NotificationService.ShowTransferRequest(
                        request.SenderNickname, request.Files.Count, request.TotalSize);

                    var dialog = new TransferDialog(request);
                    dialog.ShowDialog();
                    accepted = dialog.IsAccepted;

                    // 수락 시 자동 페어링
                    if (accepted && !settingsService.Settings.PairedDeviceIds.Contains(request.SenderDeviceId))
                    {
                        settingsService.Settings.PairedDeviceIds.Add(request.SenderDeviceId);
                        _ = settingsService.SaveAsync();
                    }
                });

                return accepted;
            };

        // 텍스트 수신 → 해당 채팅창에 전달
        transferService.TextReceived += (senderNickname, senderDeviceId, text) =>
        {
            Dispatcher.Invoke(() =>
            {
                var message = new ChatMessage
                {
                    Type = ChatMessageType.Text,
                    Direction = ChatDirection.Received,
                    Text = text
                };

                if (MainWindow is MainWindow mainWindow)
                {
                    mainWindow.DeliverIncomingMessage(senderDeviceId, senderNickname, message);
                }

                // 클립보드에도 복사
                clipboardService.SetText(text);
            });
        };

        // 파일 수신 완료 → 해당 채팅창에 전달
        transferService.FilesReceived += (senderNickname, senderDeviceId, filePaths) =>
        {
            Dispatcher.Invoke(() =>
            {
                foreach (var filePath in filePaths)
                {
                    var message = new ChatMessage
                    {
                        Type = ChatMessageType.File,
                        Direction = ChatDirection.Received,
                        FileName = System.IO.Path.GetFileName(filePath),
                        FileSize = File.Exists(filePath) ? new FileInfo(filePath).Length : 0,
                        FilePath = filePath
                    };

                    if (MainWindow is MainWindow mainWindow)
                    {
                        mainWindow.DeliverIncomingMessage(senderDeviceId, senderNickname, message);
                    }
                }

                NotificationService.ShowTransferCompleted(senderNickname, isSend: false);
            });
        };

        await discoveryService.StartAsync();
        await transferService.StartServerAsync();
        clipboardService.Start();

        // TCP 연결 시 피어 발견 → Discovery에 등록 (UDP 단방향 실패 대비)
        transferService.PeerConnected += device =>
        {
            discoveryService.AddOrUpdateDevice(device);
        };

        // TCP Ping으로 지속 채팅 연결 수립됨 → 해당 채팅창에 전달
        transferService.ChatEstablished += chatConnection =>
        {
            Dispatcher.Invoke(() =>
            {
                if (MainWindow is MainWindow mainWindow)
                {
                    mainWindow.DeliverChatConnection(chatConnection);
                }
            });
        };

            // 스마트 업데이트: 높은 버전 감지 시 자동 다운로드+적용
            var isUpdatingFlag = 0;
            discoveryService.UpdateAvailable += async (peerWithNewVersion) =>
            {
                if (Interlocked.CompareExchange(ref isUpdatingFlag, 1, 0) != 0)
                    return;

                try
                {
                    NotificationService.ShowUpdateInfo($"새 버전({peerWithNewVersion.Version}) 감지: 업데이트 다운로드 시작");

                    var newExePath = await transferService.RequestUpdateAsync(peerWithNewVersion);
                    if (newExePath is null)
                        return;

                    NotificationService.ShowUpdateInfo("업데이트 다운로드 완료: 재시작 중...");

                    await Task.Delay(2000); // 알림 표시 대기

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (MainWindow is MainWindow mainWindow)
                        {
                            mainWindow.PrepareForAppShutdown();
                        }

                        if (Core.AutoUpdater.ApplyUpdate(newExePath))
                        {
                            BeginForcedShutdown();
                        }
                        else
                        {
                            ShowUpdatePopup("업데이트 적용에 실패했습니다. 로그를 확인해 주세요.", MessageBoxImage.Warning);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Core.AppLogger.Log("App", $"업데이트 처리 실패: {ex}");
                    ShowUpdatePopup("업데이트 처리 중 오류가 발생했습니다. 로그를 확인해 주세요.", MessageBoxImage.Error);
                }
                finally
                {
                    Interlocked.Exchange(ref isUpdatingFlag, 0);
                }
            };

            // 메인 윈도우 표시
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();

            // 이전 업데이트 상태 표시 (재시작 후 가시성)
            var lastUpdateStatus = Core.AutoUpdater.ConsumeLastUpdateStatus();
            if (!string.IsNullOrWhiteSpace(lastUpdateStatus))
            {
                // 성공은 ConsumeLastUpdateStatus에서 null 처리됨. 실패/오류만 팝업으로 표시.
                ShowUpdatePopup(lastUpdateStatus);
            }
        }
        catch (Exception ex)
        {
            Core.AppLogger.Log("App", $"OnStartup 실패: {ex}");
            MessageBox.Show($"앱 시작 중 오류가 발생했습니다.\n\n{ex.Message}",
                "SimplyShare", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        IsForcedShutdown = true;
        try
        {
            if (_serviceProvider is not null)
            {
                // 서비스 정리
                var discoveryService = _serviceProvider.GetService<IDiscoveryService>();
                if (discoveryService is not null)
                    await discoveryService.StopAsync();

                var transferService = _serviceProvider.GetService<ITransferService>();
                if (transferService is not null)
                    await transferService.StopServerAsync();

                var clipboardService = _serviceProvider.GetService<IClipboardService>();
                clipboardService?.Stop();
            }
        }
        catch (Exception ex)
        {
            Core.AppLogger.Log("App", $"OnExit 정리 실패: {ex}");
        }
        finally
        {
            _serviceProvider?.Dispose();
            base.OnExit(e);
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Services
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IDiscoveryService, DiscoveryService>();
        services.AddSingleton<ITransferService, TransferService>();
        services.AddSingleton<IClipboardService, ClipboardWatcher>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SetupViewModel>();
    }
}

