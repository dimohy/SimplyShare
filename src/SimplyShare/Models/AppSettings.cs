namespace SimplyShare.Models;

/// <summary>
/// 애플리케이션 설정
/// </summary>
public sealed class AppSettings
{
    /// <summary>사용자 닉네임</summary>
    public string Nickname { get; set; } = string.Empty;

    /// <summary>장치 고유 ID (최초 실행 시 생성)</summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>UDP Discovery 포트</summary>
    public int DiscoveryPort { get; set; } = NetworkDefaults.DiscoveryPort;

    /// <summary>TCP 전송 포트</summary>
    public int TransferPort { get; set; } = NetworkDefaults.TransferPort;

    /// <summary>네트워크 대역 필터 (예: "192.168.100.*")</summary>
    public List<string> NetworkRanges { get; set; } = [];

    /// <summary>수신 파일 저장 경로</summary>
    public string DownloadPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads\SimplyShare";

    /// <summary>시작프로그램 등록 여부</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>최초 설정 완료 여부</summary>
    public bool IsSetupCompleted { get; set; }

    /// <summary>페어링된 장치 ID 목록 (자동 수락)</summary>
    public List<string> PairedDeviceIds { get; set; } = [];
}

/// <summary>
/// 네트워크 기본값
/// </summary>
public static class NetworkDefaults
{
    public const int DiscoveryPort = 52525;
    public const int TransferPort = 52526;
    public const int HeartbeatIntervalMs = 10_000;
    public const int OfflineTimeoutMs = 30_000;
    public const int FileChunkSize = 65_536; // 64KB
    public const int SocketBufferSize = 1_048_576; // 1MB
}
