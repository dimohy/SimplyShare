using SimplyShare.Core.Transfer;
using SimplyShare.Models;

namespace SimplyShare.Services;

/// <summary>
/// 전송 서비스 통합 구현 (TransferServer + TransferClient 통합)
/// </summary>
public sealed class TransferService : ITransferService, IDisposable
{
    private readonly TransferServer _server;
    private readonly TransferClient _client;

    public event Func<TransferRequest, Task<bool>>? TransferRequested;
    public event Action<TransferProgress>? ProgressChanged;

    /// <summary>텍스트 수신 이벤트 (발신자 닉네임, 발신자 DeviceId, 텍스트)</summary>
    public event Action<string, string, string>? TextReceived;

    /// <summary>파일 수신 완료 이벤트 (발신자 닉네임, 발신자 DeviceId, 파일 경로 목록)</summary>
    public event Action<string, string, IReadOnlyList<string>>? FilesReceived;

    /// <summary>TCP 연결로 인한 피어 발견 이벤트 (UDP 단방향 실패 대비)</summary>
    public event Action<DeviceInfo>? PeerConnected;

    /// <summary>Ping으로 지속 채팅 연결 수립됨 이벤트 (수신 측)</summary>
    public event Action<ChatConnection>? ChatEstablished;

    public TransferService(ISettingsService settingsService)
    {
        _server = new TransferServer(settingsService);
        _client = new TransferClient(settingsService);

        _server.TransferRequested += request =>
            TransferRequested?.Invoke(request) ?? Task.FromResult(false);
        _server.ProgressChanged += progress => ProgressChanged?.Invoke(progress);
        _server.TextReceived += (sender, deviceId, text) => TextReceived?.Invoke(sender, deviceId, text);
        _server.FilesReceived += (sender, deviceId, paths) => FilesReceived?.Invoke(sender, deviceId, paths);
        _server.PeerConnected += device => PeerConnected?.Invoke(device);
        _server.ChatEstablished += conn => ChatEstablished?.Invoke(conn);
        _client.ProgressChanged += progress => ProgressChanged?.Invoke(progress);
    }

    public Task StartServerAsync(CancellationToken cancellationToken = default)
        => _server.StartAsync(cancellationToken);

    public Task StopServerAsync(CancellationToken cancellationToken = default)
        => _server.StopAsync(cancellationToken);

    public Task SendTextAsync(DeviceInfo target, string text, CancellationToken cancellationToken = default)
        => _client.SendTextAsync(target, text, cancellationToken);

    public Task SendFilesAsync(DeviceInfo target, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        => _client.SendFilesAsync(target, paths, cancellationToken);

    public Task<string?> RequestUpdateAsync(DeviceInfo source, CancellationToken cancellationToken = default)
        => _client.RequestUpdateAsync(source, cancellationToken);

    public Task SendPingAsync(DeviceInfo target, CancellationToken cancellationToken = default)
        => _client.SendPingAsync(target, cancellationToken);

    public Task<ChatConnection> ConnectChatAsync(DeviceInfo target, CancellationToken cancellationToken = default)
        => _client.ConnectChatAsync(target, cancellationToken);

    public void Dispose()
    {
        _server.Dispose();
    }
}
