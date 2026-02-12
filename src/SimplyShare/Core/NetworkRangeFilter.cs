using System.Net;

namespace SimplyShare.Core;

/// <summary>
/// 네트워크 대역 필터 ("192.168.100.*" 형식 파싱 및 IP 매칭)
/// </summary>
public static class NetworkRangeFilter
{
    /// <summary>
    /// IP 주소가 지정된 네트워크 대역 목록에 포함되는지 확인
    /// </summary>
    public static bool IsInRange(string ipAddress, IReadOnlyList<string> ranges)
    {
        if (ranges.Count is 0)
            return true; // 대역 미설정 시 모두 허용

        if (!IPAddress.TryParse(ipAddress, out var ip))
            return false;

        var ipBytes = ip.GetAddressBytes();
        if (ipBytes.Length is not 4)
            return false; // IPv4만 지원

        foreach (var range in ranges)
        {
            if (MatchesPattern(ipBytes, range))
                return true;
        }

        return false;
    }

    /// <summary>
    /// "192.168.100.*" 패턴과 IP 바이트 매칭
    /// </summary>
    private static bool MatchesPattern(byte[] ipBytes, string pattern)
    {
        var parts = pattern.Split('.');
        if (parts.Length is not 4)
            return false;

        for (var i = 0; i < 4; i++)
        {
            if (parts[i] is "*")
                continue; // 와일드카드 — 모든 값 허용

            if (!byte.TryParse(parts[i], out var expected))
                return false;

            if (ipBytes[i] != expected)
                return false;
        }

        return true;
    }
}
