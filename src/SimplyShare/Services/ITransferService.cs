using SimplyShare.Core.Transfer;
using SimplyShare.Models;

namespace SimplyShare.Services;

/// <summary>
/// 파일/텍스트 전송 서비스
/// </summary>
public interface ITransferService
{
    /// <summary>전송 요청 수신 이벤트 (수락/거부 결정 필요)</summary>
    event Func<TransferRequest, Task<bool>>? TransferRequested;

    /// <summary>전송 진행률 변경 이벤트</summary>
    event Action<TransferProgress>? ProgressChanged;

    /// <summary>텍스트 수신 이벤트 (발신자 닉네임, 발신자 DeviceId, 텍스트)</summary>
    event Action<string, string, string>? TextReceived;

    /// <summary>파일 수신 완료 이벤트 (발신자 닉네임, 발신자 DeviceId, 파일 경로 목록)</summary>
    event Action<string, string, IReadOnlyList<string>>? FilesReceived;

    /// <summary>TCP 연결로 인한 피어 발견 이벤트 (UDP 단방향 실패 대비)</summary>
    event Action<DeviceInfo>? PeerConnected;

    /// <summary>Ping으로 지속 채팅 연결 수립됨 이벤트 (수신 측)</summary>
    event Action<ChatConnection>? ChatEstablished;

    /// <summary>전송 서버 시작 (수신 대기)</summary>
    Task StartServerAsync(CancellationToken cancellationToken = default);

    /// <summary>전송 서버 중지</summary>
    Task StopServerAsync(CancellationToken cancellationToken = default);

    /// <summary>텍스트 전송</summary>
    Task SendTextAsync(DeviceInfo target, string text, CancellationToken cancellationToken = default);

    /// <summary>파일/폴더 전송</summary>
    Task SendFilesAsync(DeviceInfo target, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>앱 업데이트 다운로드 요청</summary>
    /// <returns>다운로드된 EXE 임시 경로, 실패 시 null</returns>
    Task<string?> RequestUpdateAsync(DeviceInfo source, CancellationToken cancellationToken = default);

    /// <summary>TCP Ping 전송 — 상대방에게 자신의 존재를 알림</summary>
    Task SendPingAsync(DeviceInfo target, CancellationToken cancellationToken = default);

    /// <summary>대상 장치와 지속 채팅 연결 수립 (클라이언트 측)</summary>
    Task<ChatConnection> ConnectChatAsync(DeviceInfo target, CancellationToken cancellationToken = default);
}
