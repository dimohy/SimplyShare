using SimplyShare.Models;

namespace SimplyShare.Services;

/// <summary>
/// 장치 발견 서비스
/// </summary>
public interface IDiscoveryService
{
    /// <summary>발견된 장치 목록 변경 이벤트</summary>
    event Action<IReadOnlyList<DeviceInfo>>? DevicesChanged;

    /// <summary>더 높은 버전의 장치 발견 이벤트</summary>
    event Action<DeviceInfo>? UpdateAvailable;

    /// <summary>현재 발견된 장치 목록</summary>
    IReadOnlyList<DeviceInfo> Devices { get; }

    /// <summary>Discovery 시작</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Discovery 중지 (Goodbye 발송)</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>TCP 연결로 발견된 장치를 수동 등록 (UDP 단방향 실패 대비)</summary>
    void AddOrUpdateDevice(DeviceInfo device);
}
