namespace SimplyShare.Models;

/// <summary>
/// 네트워크에서 발견된 장치 정보
/// </summary>
public sealed record DeviceInfo
{
    /// <summary>장치 고유 ID</summary>
    public required string DeviceId { get; init; }

    /// <summary>사용자 닉네임</summary>
    public required string Nickname { get; init; }

    /// <summary>IP 주소</summary>
    public required string IpAddress { get; init; }

    /// <summary>TCP 전송 포트</summary>
    public required int Port { get; init; }

    /// <summary>프로토콜 버전</summary>
    public string Version { get; init; } = "1.0";

    /// <summary>마지막 Heartbeat 수신 시각</summary>
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    /// <summary>온라인 여부</summary>
    public bool IsOnline { get; set; } = true;
}
