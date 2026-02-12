namespace SimplyShare.Models;

/// <summary>
/// 1:1 채팅 연결 구성(입력 공유 경계 등)
/// </summary>
public sealed record ChatConfig
{
    /// <summary>
    /// 로컬에서 원격으로 넘어가는 경계 방향
    /// </summary>
    public required BoundarySide BoundarySide { get; init; }
}

/// <summary>
/// 원격 제어로 넘어가는 경계 방향
/// </summary>
public enum BoundarySide
{
    Right,
    Left,
    Top,
    Bottom
}
