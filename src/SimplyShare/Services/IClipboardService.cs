namespace SimplyShare.Services;

/// <summary>
/// 클립보드 감시 서비스
/// </summary>
public interface IClipboardService
{
    /// <summary>클립보드 텍스트 변경 이벤트</summary>
    event Action<string>? ClipboardTextChanged;

    /// <summary>현재 클립보드 텍스트</summary>
    string? CurrentText { get; }

    /// <summary>감시 시작</summary>
    void Start();

    /// <summary>감시 중지</summary>
    void Stop();

    /// <summary>텍스트를 클립보드에 복사</summary>
    void SetText(string text);
}
