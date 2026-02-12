namespace SimplyShare.Models;

/// <summary>
/// 전송 진행 상태
/// </summary>
public sealed record TransferProgress
{
    /// <summary>전송 ID</summary>
    public required string TransferId { get; init; }

    /// <summary>현재 전송 중인 파일명</summary>
    public string? CurrentFileName { get; init; }

    /// <summary>전송된 바이트</summary>
    public long BytesTransferred { get; set; }

    /// <summary>총 바이트</summary>
    public long TotalBytes { get; init; }

    /// <summary>진행률 (0.0 ~ 1.0)</summary>
    public double Progress => TotalBytes is 0 ? 0.0 : (double)BytesTransferred / TotalBytes;

    /// <summary>전송 상태</summary>
    public TransferStatus Status { get; set; } = TransferStatus.Pending;

    /// <summary>상대방 닉네임</summary>
    public string? PeerNickname { get; init; }

    /// <summary>전송 방향</summary>
    public TransferDirection Direction { get; init; }
}

/// <summary>전송 상태</summary>
public enum TransferStatus
{
    Pending,
    WaitingAcceptance,
    InProgress,
    Completed,
    Rejected,
    Failed,
    Cancelled
}

/// <summary>전송 방향</summary>
public enum TransferDirection
{
    Send,
    Receive
}
