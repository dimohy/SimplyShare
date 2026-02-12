using System.Reflection;

namespace SimplyShare.Core;

/// <summary>
/// 앱 버전 유틸리티
/// </summary>
public static class AppVersion
{
    /// <summary>현재 앱 버전 (csproj의 Version에서 가져옴)</summary>
    public static Version Current { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    /// <summary>버전 문자열 (예: "1.0.0")</summary>
    public static string CurrentString => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    /// <summary>상대 버전이 현재보다 높은지 확인</summary>
    public static bool IsNewerThan(string versionString)
    {
        if (Version.TryParse(versionString, out var other))
            return other > Current;
        return false;
    }
}
