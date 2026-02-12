namespace SimplyShare.Services;

/// <summary>
/// Windows 트레이 아이콘 풍선팁 알림 서비스 (MainWindow 트레이 아이콘 사용)
/// </summary>
public sealed class NotificationService
{
    /// <summary>텍스트 수신 알림</summary>
    public static void ShowTextReceived(string senderNickname, string textPreview)
    {
        var preview = textPreview.Length > 100 ? textPreview[..100] + "..." : textPreview;
        ShowBalloon($"{senderNickname}님의 텍스트", preview);
    }

    /// <summary>파일 전송 요청 알림</summary>
    public static void ShowTransferRequest(string senderNickname, int fileCount, long totalSize)
    {
        ShowBalloon($"{senderNickname}님의 파일 전송 요청", $"파일 {fileCount}개 ({FormatSize(totalSize)})");
    }

    /// <summary>전송 완료 알림</summary>
    public static void ShowTransferCompleted(string peerNickname, bool isSend)
    {
        var direction = isSend ? "전송" : "수신";
        ShowBalloon("SimplyShare", $"{peerNickname}님과의 {direction}이 완료되었습니다.");
    }

    /// <summary>업데이트 진행 알림</summary>
    public static void ShowUpdateInfo(string message)
        => ShowBalloon("SimplyShare 업데이트", message);

    private static void ShowBalloon(string title, string message)
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null)
                return;

            void Show()
            {
                if (app.MainWindow is MainWindow mw)
                {
                    mw.ShowBalloonTip(title, message);
                }
            }

            if (app.Dispatcher.CheckAccess())
            {
                Show();
            }
            else
            {
                _ = app.Dispatcher.BeginInvoke(Show);
            }
        }
        catch (Exception ex)
        {
            Core.AppLogger.Log("Notification", $"알림 표시 실패: {ex}");
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:F1} {units[unit]}";
    }
}
