using System;
using System.Security.Cryptography;
using System.Text;

namespace HorizonServiceMonitor;

/// <summary>
/// 법인 모니터링 계정 비밀번호 보관용 DPAPI 래퍼.
/// Windows 사용자 프로필(CurrentUser) 단위로 암호화되므로 DB 파일을 복사해가도 복호화 불가.
/// (다른 PC/계정으로 DB를 옮기면 비밀번호만 재입력하면 된다.)
/// </summary>
public static class Crypto
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HorizonServiceMonitor.v1");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        if (!OperatingSystem.IsWindows()) return "plain:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return "dpapi:" + Convert.ToBase64String(enc);
    }

    /// <summary>복호화. 실패(다른 사용자/PC의 DB 등) 시 빈 문자열 — 호출부는 '비밀번호 재입력 필요'로 처리.</summary>
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            if (stored.StartsWith("plain:", StringComparison.Ordinal))
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored.Substring(6)));
            if (stored.StartsWith("dpapi:", StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows()) return "";
                var dec = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(6)), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            return ""; // 알 수 없는 형식
        }
        catch { return ""; }
    }
}
