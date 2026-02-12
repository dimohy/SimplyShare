using System.IO;

namespace SimplyShare.Core;

/// <summary>
/// 간단한 파일 로거 (Release에서도 동작)
/// %LOCALAPPDATA%/SimplyShare/logs/ 에 기록
/// </summary>
public static class AppLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimplyShare", "logs");

    private static readonly string LogFile = Path.Combine(
        LogDir, $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

    private static readonly object Lock = new();

    static AppLogger()
    {
        Directory.CreateDirectory(LogDir);
    }

    public static void Log(string tag, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] {message}";

        lock (Lock)
        {
            try
            {
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
            catch
            {
                // 로깅 실패는 무시
            }
        }
    }
}
