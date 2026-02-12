namespace SimplyShare.Models;

/// <summary>
/// 채팅 메시지 (텍스트 또는 파일 전송 기록)
/// </summary>
public sealed record ChatMessage
{
    /// <summary>메시지 ID</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>메시지 타입</summary>
    public required ChatMessageType Type { get; init; }

    /// <summary>방향 (전송/수신)</summary>
    public required ChatDirection Direction { get; init; }

    /// <summary>텍스트 내용</summary>
    public string? Text { get; init; }

    /// <summary>파일명 (파일 전송 시)</summary>
    public string? FileName { get; init; }

    /// <summary>파일 크기 (파일 전송 시)</summary>
    public long FileSize { get; init; }

    /// <summary>파일 저장 경로 (수신한 파일)</summary>
    public string? FilePath { get; init; }

    /// <summary>타임스탬프</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>전송 상태</summary>
    public ChatMessageStatus Status { get; set; } = ChatMessageStatus.Completed;

    /// <summary>전송 진행률 (0~1)</summary>
    public double Progress { get; set; } = 1.0;
}

/// <summary>메시지 타입</summary>
public enum ChatMessageType
{
    /// <summary>텍스트 메시지</summary>
    Text,
    /// <summary>파일 전송</summary>
    File,
    /// <summary>시스템 메시지 (연결됨, 페어링 등)</summary>
    System
}

/// <summary>메시지 방향</summary>
public enum ChatDirection
{
    /// <summary>내가 보낸 메시지</summary>
    Sent,
    /// <summary>상대가 보낸 메시지</summary>
    Received
}

/// <summary>메시지 상태</summary>
public enum ChatMessageStatus
{
    Sending,
    Completed,
    Failed
}
