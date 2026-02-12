namespace SimplyShare.Models;

/// <summary>
/// 전송 프로토콜 메시지
/// </summary>
public sealed record TransferMessage
{
    /// <summary>메시지 타입</summary>
    public required TransferMessageType Type { get; init; }

    /// <summary>페이로드 (타입별 다른 데이터)</summary>
    public string? Payload { get; init; }
}

/// <summary>전송 프로토콜 메시지 타입</summary>
public enum TransferMessageType
{
    KeyExchange,
    TransferRequest,
    TransferAccepted,
    TransferRejected,
    Data,
    Complete,
    Error,
    Cancel
}
