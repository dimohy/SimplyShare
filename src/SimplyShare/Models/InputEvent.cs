namespace SimplyShare.Models;

/// <summary>
/// 원격 입력 공유 이벤트 (ChatConnection 전용)
/// </summary>
public sealed record InputEvent
{
    /// <summary>이벤트 종류</summary>
    public required InputEventKind Kind { get; init; }

    /// <summary>인자 1 (종류별 의미 다름)</summary>
    public int Arg1 { get; init; }

    /// <summary>인자 2 (종류별 의미 다름)</summary>
    public int Arg2 { get; init; }

    /// <summary>인자 3 (종류별 의미 다름)</summary>
    public int Arg3 { get; init; }
}

/// <summary>
/// 입력 이벤트 종류
/// </summary>
public enum InputEventKind
{
    MouseMove,
    MouseDown,
    MouseUp,
    MouseWheel,
    KeyDown,
    KeyUp
}
