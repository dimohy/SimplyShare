using SimplyShare.Models;

namespace SimplyShare.Services;

/// <summary>
/// 앱 설정 관리 서비스
/// </summary>
public interface ISettingsService
{
    /// <summary>현재 설정</summary>
    AppSettings Settings { get; }

    /// <summary>설정 파일에서 로드</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>설정 파일에 저장</summary>
    Task SaveAsync(CancellationToken cancellationToken = default);
}
