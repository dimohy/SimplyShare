using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using SimplyShare.Core;
using SimplyShare.Core.Transfer;
using SimplyShare.Models;
using SimplyShare.ViewModels;
using SimplyShare.Views;

namespace SimplyShare;

/// <summary>
/// 메인 윈도우 — 장치 목록 + 시스템 트레이
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private bool _isExiting;

    /// <summary>열려있는 채팅창 관리 (DeviceId → ChatWindow)</summary>
    private readonly Dictionary<string, ChatWindow> _chatWindows = [];

    /// <summary>트레이 아이콘으로 풍선 팁 표시</summary>
    public void ShowBalloonTip(string title, string message, int timeoutMs = 3000)
    {
        if (_notifyIcon is null) return;
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(timeoutMs);
    }

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = App.Current.Services.GetRequiredService<MainViewModel>();
        DataContext = _viewModel;

        var settingsService = App.Current.Services.GetRequiredService<Services.ISettingsService>();
        NicknameText.Text = $"내 닉네임: {settingsService.Settings.Nickname}";
        VersionText.Text = $"v{Core.AppVersion.CurrentString}";

        var localIp = TryGetLocalIpv4(settingsService.Settings.NetworkRanges);
        if (localIp is not null)
        {
            LocalIpText.Text = $"내 IP: {localIp}";
        }
        else
        {
            AppLogger.Log("MainWindow", $"로컬 IP 미검출 — NetworkRanges=[{string.Join(", ", settingsService.Settings.NetworkRanges)}]");
        }

        InitializeNotifyIcon();
    }

    private static string? TryGetLocalIpv4(IReadOnlyList<string> networkRanges)
    {
        if (networkRanges.Count is 0)
            return null;

        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus is not OperationalStatus.Up)
                continue;

            if (iface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            foreach (var addr in iface.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily is not AddressFamily.InterNetwork)
                    continue;

                var ip = addr.Address.ToString();
                if (Core.NetworkRangeFilter.IsInRange(ip, networkRanges))
                    return ip;
            }
        }

        return null;
    }

    // --- 채팅창 열기 ---

    public void OpenChatWindow(DeviceInfo device)
    {
        if (_chatWindows.TryGetValue(device.DeviceId, out var existing))
        {
            existing.Activate();
            return;
        }

        var chatVm = new ChatViewModel(device,
            App.Current.Services.GetRequiredService<Services.ITransferService>(),
            App.Current.Services.GetRequiredService<Services.ISettingsService>(),
            App.Current.Services.GetRequiredService<Services.IClipboardService>());
        var chatWindow = new ChatWindow(chatVm);
        chatWindow.Closed += (_, _) => _chatWindows.Remove(device.DeviceId);
        _chatWindows[device.DeviceId] = chatWindow;
        chatWindow.Show();

        // ★ 지속 채팅 연결 수립 시도 (백그라운드)
        _ = Task.Run(async () =>
        {
            try
            {
                var transferService = App.Current.Services.GetRequiredService<Services.ITransferService>();
                var chatConn = await transferService.ConnectChatAsync(device);

                AppLogger.Log("MainWindow", $"ChatConnection 수립 성공 → {device.Nickname}({device.IpAddress})");

                Dispatcher.Invoke(() =>
                {
                    // 이미 다른 경로로 연결됐을 수 있음 (상대방이 먼저 Ping)
                    if (!chatVm.IsChatConnected)
                    {
                        chatVm.SetChatConnection(chatConn, isInitiator: true);
                        chatConn.StartReceiveLoop();
                    }
                    else
                    {
                        chatConn.Dispose(); // 중복 연결 정리
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.Log("MainWindow", $"ChatConnection 수립 실패 → {device.Nickname}: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    chatVm.StatusMessage = "연결 대기 중... (상대방이 접속하면 자동 연결)";
                });
            }
        });
    }

    /// <summary>수신 ChatConnection을 해당 채팅창에 전달 (없으면 채팅창 자동 생성)</summary>
    public void DeliverChatConnection(ChatConnection chatConnection)
    {
        // 장치 정보 찾기
        var device = _viewModel.Devices.FirstOrDefault(d => d.DeviceId == chatConnection.PeerDeviceId);
        if (device is null)
        {
            // Discovery에 없으면 ChatConnection 정보로 임시 DeviceInfo 생성
            device = new DeviceInfo
            {
                DeviceId = chatConnection.PeerDeviceId,
                Nickname = chatConnection.PeerNickname,
                IpAddress = "unknown",
                Port = 0,
                IsOnline = true
            };
        }

        if (!_chatWindows.TryGetValue(chatConnection.PeerDeviceId, out var chatWindow))
        {
            // 채팅창 생성 (ConnectChatAsync 없이 — 이미 상대방이 연결해줌)
            var chatVm = new ChatViewModel(device,
                App.Current.Services.GetRequiredService<Services.ITransferService>(),
                App.Current.Services.GetRequiredService<Services.ISettingsService>(),
                App.Current.Services.GetRequiredService<Services.IClipboardService>());
            chatWindow = new ChatWindow(chatVm);
            chatWindow.Closed += (_, _) => _chatWindows.Remove(device.DeviceId);
            _chatWindows[device.DeviceId] = chatWindow;
            chatWindow.Show();
        }

        if (!chatWindow.ViewModel.IsChatConnected)
        {
            chatWindow.ViewModel.SetChatConnection(chatConnection, isInitiator: false);
            chatConnection.StartReceiveLoop();
            chatWindow.Activate();
        }
        else
        {
            chatConnection.Dispose(); // 이미 연결이 있으면 정리
        }
    }

    /// <summary>수신 메시지를 해당 채팅창에 전달 (없으면 채팅창 자동 생성)</summary>
    public void DeliverIncomingMessage(string senderDeviceId, string senderNickname, ChatMessage message)
    {
        // 장치 정보 찾기
        var device = _viewModel.Devices.FirstOrDefault(d => d.DeviceId == senderDeviceId);
        if (device is null)
            return;

        if (!_chatWindows.TryGetValue(senderDeviceId, out var chatWindow))
        {
            OpenChatWindow(device);
            chatWindow = _chatWindows[senderDeviceId];
        }

        chatWindow.ViewModel.AddReceivedMessage(message);
        chatWindow.Activate();
    }

    private void DeviceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceList.SelectedItem is DeviceInfo device)
        {
            OpenChatWindow(device);
        }
    }

    // --- 시스템 트레이 ---

    private void InitializeNotifyIcon()
    {
        var iconStream = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        var icon = iconStream is not null
            ? new System.Drawing.Icon(iconStream)
            : System.Drawing.SystemIcons.Application;

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "SimplyShare",
            Icon = icon,
            Visible = true
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => RestoreWindow());
        menu.Items.Add("설정", null, (_, _) => OpenSettings());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitApplication());

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => RestoreWindow();
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        Activate();
    }

    private void ExitApplication()
    {
        _isExiting = true;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        Application.Current.Shutdown();
    }

    /// <summary>앱 내부(업데이트 등)에서 강제 종료가 필요할 때 호출</summary>
    public void PrepareForAppShutdown()
    {
        _isExiting = true;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState is WindowState.Minimized)
        {
            Hide();
            ShowInTaskbar = false;
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.IsForcedShutdown)
            return;

        if (_isExiting)
            return;

        e.Cancel = true;
        WindowState = WindowState.Minimized;
    }

    // --- 설정 ---

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private void OpenSettings()
    {
        var vm = App.Current.Services.GetRequiredService<SettingsViewModel>();
        var window = new SettingsWindow(vm) { Owner = this };
        window.ShowDialog();
    }
}