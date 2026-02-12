namespace SimplyShare.Models;

/// <summary>
/// 1:1 공유 기능 동의 상태
/// </summary>
public sealed record SharePreferences
{
    /// <summary>입력 공유 허용 여부</summary>
    public bool InputSharingEnabled { get; init; }

    /// <summary>클립보드 공유 허용 여부</summary>
    public bool ClipboardSharingEnabled { get; init; }
}
