namespace SimplyShare.Models;

/// <summary>
/// 전송 요청 정보
/// </summary>
public sealed record TransferRequest
{
    /// <summary>전송 타입</summary>
    public required TransferType Type { get; init; }

    /// <summary>전송자 정보</summary>
    public required string SenderNickname { get; init; }

    /// <summary>전송자 장치 ID</summary>
    public required string SenderDeviceId { get; init; }

    /// <summary>전송자 TCP 전송 포트</summary>
    public int SenderPort { get; init; }

    /// <summary>텍스트 내용 (텍스트 전송 시)</summary>
    public string? TextContent { get; init; }

    /// <summary>파일 정보 목록 (파일 전송 시)</summary>
    public IReadOnlyList<FileTransferInfo> Files { get; init; } = [];

    /// <summary>총 전송 크기 (bytes)</summary>
    public long TotalSize { get; init; }
}

/// <summary>전송 타입</summary>
public enum TransferType
{
    // NOTE: 프로토콜 호환성을 위해 값을 고정한다.
    Text = 0,
    File = 1,
    Files = 2,
    /// <summary>앱 업데이트 요청 (서버가 자신의 EXE를 전송)</summary>
    AppUpdate = 3,
    /// <summary>TCP Ping — 상대방에게 자신의 존재를 알림 (UDP 단방향 실패 대비)</summary>
    Ping = 4,

    /// <summary>1:1 연결 설정 동기화 (ChatConnection 전용)</summary>
    ChatConfig = 5,
    /// <summary>클립보드 텍스트 동기화 (ChatConnection 전용)</summary>
    ClipboardText = 6,
    /// <summary>마우스/키보드 입력 이벤트 (ChatConnection 전용)</summary>
    InputEvent = 7,
    /// <summary>1:1 연결 창 종료 요청 (ChatConnection 전용)</summary>
    ChatCloseRequest = 8,
    /// <summary>1:1 연결 창 종료 수락 (ChatConnection 전용)</summary>
    ChatCloseAccept = 9,
    /// <summary>1:1 연결 창 종료 거부 (ChatConnection 전용)</summary>
    ChatCloseReject = 10,

    /// <summary>원격 제어 즉시 종료 신호 (양쪽 동시 해제)</summary>
    RemoteControlStop = 11,

    /// <summary>양측 공유 기능 동의 상태 동기화</summary>
    SharePreferences = 12
}

/// <summary>전송할 파일 정보</summary>
public sealed record FileTransferInfo
{
    /// <summary>파일 상대 경로 (폴더 전송 시 폴더 구조 유지용)</summary>
    public required string RelativePath { get; init; }

    /// <summary>파일 크기 (bytes)</summary>
    public required long Size { get; init; }

    /// <summary>디렉토리 여부</summary>
    public bool IsDirectory { get; init; }
}
