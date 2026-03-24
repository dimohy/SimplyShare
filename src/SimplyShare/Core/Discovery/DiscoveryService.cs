using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SimplyShare.Models;

namespace SimplyShare.Core.Discovery;

/// <summary>
/// UDP 브로드캐스트 기반 장치 발견 서비스
/// </summary>
public sealed class DiscoveryService : Services.IDiscoveryService, IDisposable
{
    private const int InitialBurstCount = 3;
    private const int InitialBurstIntervalMs = 80;
    private const int NetworkChangeStabilizeDelayMs = 300;
    private const int NetworkChangeBurstCount = 2;
    private const int NetworkChangeBurstIntervalMs = 80;
    private const int NewDeviceBurstCount = 2;
    private const int NewDeviceBurstIntervalMs = 80;

    private readonly Services.ISettingsService _settingsService;
    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new();
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Task? _heartbeatTask;
    private Task? _cleanupTask;
    private CancellationTokenSource? _networkChangeResendCts;

    private readonly record struct BroadcastTarget(IPAddress LocalAddress, IPEndPoint BroadcastEndPoint);

    public event Action<IReadOnlyList<DeviceInfo>>? DevicesChanged;

    /// <summary>더 높은 버전의 장치 발견 이벤트 (DeviceInfo)</summary>
    public event Action<DeviceInfo>? UpdateAvailable;

    public IReadOnlyList<DeviceInfo> Devices => [.. _devices.Values.Where(d => d.IsOnline)];

    public DiscoveryService(Services.ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsService.Settings;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _udpClient = new UdpClient();
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, settings.DiscoveryPort));
        _udpClient.EnableBroadcast = true;

        AppLogger.Log("Discovery", $"시작 — DeviceId={settings.DeviceId}, 닉네임={settings.Nickname}, 포트={settings.DiscoveryPort}");
        AppLogger.Log("Discovery", $"NetworkRanges=[{string.Join(", ", settings.NetworkRanges)}]");

        // 네트워크 변경 감지 (VPN 연결/해제 등)
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;

        // 수신 태스크 시작
        _listenTask = ListenAsync(_cts.Token);

        // 초기 Discovery 발송 (3회 빠르게)
        for (var i = 0; i < InitialBurstCount; i++)
        {
            await SendMessageAsync(DiscoveryMessageType.Discovery, cancellationToken);
            if (i < InitialBurstCount - 1)
                await Task.Delay(InitialBurstIntervalMs, cancellationToken);
        }

        AppLogger.Log("Discovery", "초기 브로드캐스트 3회 완료");

        // Heartbeat 태스크 시작
        _heartbeatTask = HeartbeatLoopAsync(_cts.Token);

        // 오프라인 정리 태스크 시작
        _cleanupTask = CleanupLoopAsync(_cts.Token);
    }

    /// <summary>네트워크 인터페이스 변경 시 (VPN 연결/해제 등) Discovery 재발송</summary>
    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        AppLogger.Log("Discovery", "네트워크 변경 감지! Discovery 재발송...");

        _networkChangeResendCts?.Cancel();
        _networkChangeResendCts?.Dispose();

        var linkedToken = _cts?.Token ?? CancellationToken.None;
        _networkChangeResendCts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken);
        var token = _networkChangeResendCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(NetworkChangeStabilizeDelayMs, token); // 네트워크 안정화 대기

                for (var i = 0; i < NetworkChangeBurstCount; i++)
                {
                    await SendMessageAsync(DiscoveryMessageType.Discovery, token);
                    if (i < NetworkChangeBurstCount - 1)
                        await Task.Delay(NetworkChangeBurstIntervalMs, token);
                }
                AppLogger.Log("Discovery", "네트워크 변경 후 Discovery 재발송 완료");
            }
            catch (OperationCanceledException)
            {
                // 종료 중
            }
            catch (Exception ex)
            {
                AppLogger.Log("Discovery", $"네트워크 변경 후 Discovery 재발송 실패: {ex.Message}");
            }
        }, token);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        AppLogger.Log("Discovery", "종료 시작 — Goodbye 발송");

        // Goodbye 메시지 발송
        await SendMessageAsync(DiscoveryMessageType.Goodbye, cancellationToken);

        _cts?.Cancel();
        _networkChangeResendCts?.Cancel();

        try
        {
            var tasks = new[] { _listenTask, _heartbeatTask, _cleanupTask }
                .Where(t => t is not null)
                .Cast<Task>()
                .ToArray();

            if (tasks.Length > 0)
            {
                await Task.WhenAny(
                    Task.WhenAll(tasks),
                    Task.Delay(1000, CancellationToken.None));
            }
        }
        catch
        {
            // 종료 경로에서는 예외를 전파하지 않음
        }

        _udpClient?.Dispose();
        _udpClient = null;

        _cts?.Dispose();
        _cts = null;

        _networkChangeResendCts?.Dispose();
        _networkChangeResendCts = null;

        AppLogger.Log("Discovery", "종료 완료");
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        AppLogger.Log("Discovery", "수신 루프 시작");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient!.ReceiveAsync(cancellationToken);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var message = JsonSerializer.Deserialize(json, AppJsonContext.Default.DiscoveryMessage);

                if (message is null)
                {
                    AppLogger.Log("Discovery", "역직렬화 실패 (null)");
                    continue;
                }

                // 자기 자신 무시
                if (message.DeviceId == _settingsService.Settings.DeviceId)
                    continue;

                // 네트워크 대역 필터링
                var senderIp = result.RemoteEndPoint.Address.ToString();
                if (!NetworkRangeFilter.IsInRange(senderIp, _settingsService.Settings.NetworkRanges))
                {
                    AppLogger.Log("Discovery", $"대역 필터 차단: {senderIp} from {message.Nickname}");
                    continue;
                }

                AppLogger.Log("Discovery", $"수신 <- {message.Type} from {message.Nickname}({message.DeviceId[..6]}) @ {senderIp}");
                await ProcessMessageAsync(message, senderIp);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Log("Discovery", $"수신 루프 예외: {ex.Message}");
            }
        }

        AppLogger.Log("Discovery", "수신 루프 종료");
    }

    private async Task ProcessMessageAsync(DiscoveryMessage message, string senderIp)
    {
        switch (message.Type)
        {
            case DiscoveryMessageType.Discovery:
            case DiscoveryMessageType.Heartbeat:
                // 신규 장치인지 확인 (오프라인→온라인 복귀도 신규 취급)
                var isNewDevice = !_devices.TryGetValue(message.DeviceId, out var existingDevice)
                                  || !existingDevice.IsOnline;

                var device = new DeviceInfo
                {
                    DeviceId = message.DeviceId,
                    Nickname = message.Nickname,
                    IpAddress = senderIp,
                    Port = message.Port,
                    Version = message.Version,
                    LastSeen = DateTime.UtcNow,
                    IsOnline = true
                };

                _devices.AddOrUpdate(message.DeviceId, device, (_, _) => device);
                NotifyDevicesChanged();

                // 높은 버전 감지 시 업데이트 이벤트
                if (AppVersion.IsNewerThan(message.Version))
                {
                    UpdateAvailable?.Invoke(device);
                }

                // 신규 장치 발견 시 → 나도 Discovery 브로드캐스트 + 유니캐스트 (상대방도 나를 알 수 있도록)
                if (isNewDevice)
                {
                    AppLogger.Log("Discovery", $"★ 신규 장치! {message.Nickname}({message.DeviceId[..6]}) @ {senderIp}");
                    try
                    {
                        // 브로드캐스트 3회
                        for (var i = 0; i < NewDeviceBurstCount; i++)
                        {
                            await SendMessageAsync(DiscoveryMessageType.Discovery, CancellationToken.None);
                            if (i < NewDeviceBurstCount - 1)
                                await Task.Delay(NewDeviceBurstIntervalMs);
                        }
                        AppLogger.Log("Discovery", $"응답 브로드캐스트 {NewDeviceBurstCount}회 완료");

                        // 유니캐스트 N회 (브로드캐스트 역방향 실패 대비)
                        for (var i = 0; i < NewDeviceBurstCount; i++)
                        {
                            await SendDirectMessageAsync(DiscoveryMessageType.Discovery, senderIp, CancellationToken.None);
                            if (i < NewDeviceBurstCount - 1)
                                await Task.Delay(NewDeviceBurstIntervalMs);
                        }
                        AppLogger.Log("Discovery", $"유니캐스트 응답 {NewDeviceBurstCount}회 완료 -> {senderIp}");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Log("Discovery", $"응답 발송 실패: {ex.Message}");
                    }
                }
                break;

            case DiscoveryMessageType.Goodbye:
                if (_devices.TryGetValue(message.DeviceId, out var existing))
                {
                    existing.IsOnline = false;
                    NotifyDevicesChanged();
                    AppLogger.Log("Discovery", $"{message.Nickname} 오프라인 처리 (Goodbye)");
                }
                break;
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(NetworkDefaults.HeartbeatIntervalMs, cancellationToken);
                await SendMessageAsync(DiscoveryMessageType.Heartbeat, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, cancellationToken);

                var now = DateTime.UtcNow;
                var changed = false;

                foreach (var kvp in _devices)
                {
                    if (kvp.Value.IsOnline &&
                        (now - kvp.Value.LastSeen).TotalMilliseconds > NetworkDefaults.OfflineTimeoutMs)
                    {
                        kvp.Value.IsOnline = false;
                        changed = true;
                        AppLogger.Log("Discovery", $"{kvp.Value.Nickname} 타임아웃 오프라인 처리");
                    }
                }

                if (changed)
                    NotifyDevicesChanged();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private byte[] BuildMessageBytes(DiscoveryMessageType type)
    {
        var settings = _settingsService.Settings;
        var message = new DiscoveryMessage
        {
            Type = type,
            Nickname = settings.Nickname,
            DeviceId = settings.DeviceId,
            Port = settings.TransferPort,
            Version = AppVersion.CurrentString
        };

        var json = JsonSerializer.Serialize(message, AppJsonContext.Default.DiscoveryMessage);
        return Encoding.UTF8.GetBytes(json);
    }

    private async Task SendMessageAsync(DiscoveryMessageType type, CancellationToken cancellationToken)
    {
        if (_udpClient is null)
            return;

        var bytes = BuildMessageBytes(type);

        // VPN 등으로 기본 라우팅이 바뀌어도, 설정 대역에 매칭되는 로컬 인터페이스에서 직접 송신
        var targets = GetBroadcastTargets();
        foreach (var target in targets)
        {
            try
            {
                using var sender = new UdpClient(new IPEndPoint(target.LocalAddress, 0));
                sender.EnableBroadcast = true;
                await sender.SendAsync(bytes, target.BroadcastEndPoint, cancellationToken);
            }
            catch (Exception ex)
            {
                AppLogger.Log("Discovery", $"브로드캐스트 전송 실패 ({target.BroadcastEndPoint} from {target.LocalAddress}): {ex.Message}");
            }
        }
    }

    /// <summary>네트워크 설정에 매칭되는 인터페이스의 서브넷 브로드캐스트 주소들을 계산</summary>
    private List<BroadcastTarget> GetBroadcastTargets()
    {
        var port = _settingsService.Settings.DiscoveryPort;
        var ranges = _settingsService.Settings.NetworkRanges;
        var targets = new List<BroadcastTarget>();

        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus is not OperationalStatus.Up)
                    continue;

                // VPN(Tunnel) 인터페이스는 설정 대역과 무관하게 라우팅에 영향을 줄 수 있어 기본 제외
                if (iface.NetworkInterfaceType is NetworkInterfaceType.Tunnel)
                    continue;

                foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily is not AddressFamily.InterNetwork)
                        continue;

                    // 이 인터페이스의 IP가 설정된 네트워크 대역에 매칭되는지 확인
                    var ipStr = addr.Address.ToString();
                    if (!NetworkRangeFilter.IsInRange(ipStr, ranges))
                        continue;

                    // 서브넷 브로드캐스트 = IP | ~Mask
                    var ipBytes = addr.Address.GetAddressBytes();
                    var maskBytes = addr.IPv4Mask.GetAddressBytes();
                    var broadcastBytes = new byte[4];
                    for (var i = 0; i < 4; i++)
                        broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);

                    var broadcastIp = new IPAddress(broadcastBytes);
                    targets.Add(new BroadcastTarget(addr.Address, new IPEndPoint(broadcastIp, port)));
                    AppLogger.Log("Discovery", $"브로드캐스트 대상: {broadcastIp} (from {ipStr}, {iface.Name})");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log("Discovery", $"인터페이스 스캔 실패: {ex.Message}");
        }

        if (targets.Count is 0)
        {
            AppLogger.Log("Discovery", "매칭 인터페이스 없음 — 브로드캐스트 불가");
        }

        return targets;
    }

    /// <summary>특정 IP에 직접 유니캐스트 전송 (브로드캐스트 역방향 실패 대비)</summary>
    private async Task SendDirectMessageAsync(DiscoveryMessageType type, string targetIp, CancellationToken cancellationToken)
    {
        if (_udpClient is null)
            return;

        var bytes = BuildMessageBytes(type);
        var targetEp = new IPEndPoint(IPAddress.Parse(targetIp), _settingsService.Settings.DiscoveryPort);

        var targets = GetBroadcastTargets();
        if (targets.Count is 0)
            return;

        // 가장 첫 매칭 인터페이스에서 유니캐스트 전송
        var localAddress = targets[0].LocalAddress;
        using var sender = new UdpClient(new IPEndPoint(localAddress, 0));
        await sender.SendAsync(bytes, targetEp, cancellationToken);
    }

    private void NotifyDevicesChanged()
    {
        DevicesChanged?.Invoke(Devices);
    }

    /// <summary>TCP 연결로 발견된 장치를 수동 등록 (UDP 단방향 실패 대비)</summary>
    public void AddOrUpdateDevice(DeviceInfo device)
    {
        // 자기 자신 무시
        if (device.DeviceId == _settingsService.Settings.DeviceId)
            return;

        var isNew = !_devices.TryGetValue(device.DeviceId, out var existing) || !existing.IsOnline;

        _devices.AddOrUpdate(device.DeviceId, device, (_, _) => device with { LastSeen = DateTime.UtcNow });
        NotifyDevicesChanged();

        if (isNew)
        {
            AppLogger.Log("Discovery", $"★ TCP Ping으로 장치 등록: {device.Nickname}({device.DeviceId[..Math.Min(6, device.DeviceId.Length)]}) @ {device.IpAddress}:{device.Port}");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _udpClient?.Dispose();
    }
}
