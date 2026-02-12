namespace SimplyShare.Models;

/// <summary>
/// Discovery 프로토콜 메시지
/// </summary>
public sealed record DiscoveryMessage
{
    /// <summary>메시지 타입</summary>
    public required DiscoveryMessageType Type { get; init; }

    /// <summary>사용자 닉네임</summary>
    public required string Nickname { get; init; }

    /// <summary>장치 고유 ID</summary>
    public required string DeviceId { get; init; }

    /// <summary>TCP 전송 포트</summary>
    public required int Port { get; init; }

    /// <summary>프로토콜 버전</summary>
    public string Version { get; init; } = "1.0";
}

/// <summary>Discovery 메시지 타입</summary>
public enum DiscoveryMessageType
{
    Discovery,
    Heartbeat,
    Goodbye
}
