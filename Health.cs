using System;
using System.Collections.Generic;
using System.Linq;

namespace HorizonServiceMonitor;

/// <summary>상태 판정 임계값(설정에서 로드).</summary>
public sealed class Thresholds
{
    public int CertWarnDays { get; set; } = 30;    // 인증서 만료 경고(일)
    public int RdsLoadWarn { get; set; } = 90;     // 세션호스트 부하지수 경고(%)
    public static Thresholds Load(Database db) => new()
    {
        CertWarnDays = db.GetIntSetting("certWarnDays", 30),
        RdsLoadWarn = db.GetIntSetting("rdsLoadWarn", 90),
    };
}

/// <summary>
/// 법인 스냅샷 → 종합 상태(Up/Warn/Down) + 사유 문자열 산출.
/// 규칙:
///  - Down: API 수집 자체가 실패(커넥션서버 정보 없음) 또는 커넥션서버 전멸.
///  - Warn: 구성요소 일부 이상(서비스/게이트웨이/팜/세션호스트/이벤트DB/AD/vCenter/SAML),
///          인증서 임박, 문제 머신 존재, 세션호스트 고부하, 일부 엔드포인트 수집 실패.
///  - Up: 위 어느 것도 아님.
/// 운영자가 의도적으로 비활성화(Enabled=false)한 세션호스트는 경고로 치지 않는다.
/// </summary>
public static class HealthEvaluator
{
    public static void Evaluate(CorpSnapshot s, Thresholds t)
    {
        // 수집 자체가 실패한 경우(FetchSnapshot에서 Error 설정, CS 데이터 없음) → Down 유지.
        if (s.ConnectionServers.Count == 0 && !string.IsNullOrEmpty(s.Error))
        {
            s.Status = HealthStatus.Down;
            return;
        }

        var reasons = new List<string>();

        // 커넥션서버 & 내부 서비스
        // RESTART_REQUIRED(v3 enum)는 서비스 자체는 동작 중 — Down('전멸') 판정에서 제외하고 Warn 사유로만.
        var csBad = s.ConnectionServers.Where(c => !Healthy.Cs(c.Status)).ToList();
        var csDead = csBad.Where(c => !string.Equals(c.Status?.Trim(), "RESTART_REQUIRED", StringComparison.OrdinalIgnoreCase)).ToList();
        if (s.CsTotal > 0 && csDead.Count == s.CsTotal)
        {
            s.Status = HealthStatus.Down;
            s.Error = "커넥션서버 전체 이상: " + string.Join(", ", csDead.Select(c => $"{c.Name}={c.Status}"));
            return;
        }
        if (csBad.Count > 0)
            reasons.Add("CS 이상 " + string.Join(", ", csBad.Select(c => $"{c.Name}({c.Status})")));

        var svcBad = s.ConnectionServers
            .SelectMany(c => c.Services.Where(v => !Healthy.CsService(v.Status)).Select(v => $"{c.Name}:{v.Name}"))
            .ToList();
        if (svcBad.Count > 0) reasons.Add("서비스 이상 " + string.Join(", ", svcBad.Take(5)) + (svcBad.Count > 5 ? $" 외 {svcBad.Count - 5}" : ""));

        // 인증서(커넥션서버 인증서 + 접속 URL TLS)
        foreach (var c in s.ConnectionServers)
        {
            if (c.CertValid == false) { reasons.Add($"{c.Name} 인증서 무효"); continue; }
            if (c.CertExpiryUtc is DateTime exp)
            {
                var days = (int)Math.Floor((exp - DateTime.UtcNow).TotalDays);
                if (days <= t.CertWarnDays) reasons.Add(days <= 0 ? $"{c.Name} 인증서 만료" : $"{c.Name} 인증서 {days}일 남음");
            }
        }
        if (s.ApiCertExpiryDays is int apiDays && apiDays <= t.CertWarnDays)
            reasons.Add(apiDays <= 0 ? "접속 URL 인증서 만료" : $"접속 URL 인증서 {apiDays}일 남음");

        // 게이트웨이
        var gwBad = s.Gateways.Where(g => !Healthy.Gateway(g.Status)).Select(g => $"{g.Name}({g.Status})").ToList();
        if (gwBad.Count > 0) reasons.Add("게이트웨이 이상 " + string.Join(", ", gwBad));

        // 팜(DISABLED는 운영자 의도 — 경고 제외)
        var farmBad = s.Farms
            .Where(f => !Healthy.Farm(f.Status) && !string.Equals(f.Status, "DISABLED", StringComparison.OrdinalIgnoreCase))
            .Select(f => $"{f.Name}({f.Status})").ToList();
        if (farmBad.Count > 0) reasons.Add("팜 이상 " + string.Join(", ", farmBad));

        // 풀 헬스(monitor/desktops 제공 시)
        var poolBad = s.DesktopPools.Where(Healthy.PoolProblem).Select(p => $"{p.Name}({p.Status})").ToList();
        if (poolBad.Count > 0) reasons.Add("풀 이상 " + string.Join(", ", poolBad.Take(5)) + (poolBad.Count > 5 ? $" 외 {poolBad.Count - 5}" : ""));

        // 세션호스트(비활성 제외)
        var rdsBad = s.RdsServers.Where(RdsProblem).Select(r => $"{r.Name}({r.Status})").ToList();
        if (rdsBad.Count > 0) reasons.Add("세션호스트 이상 " + string.Join(", ", rdsBad.Take(5)) + (rdsBad.Count > 5 ? $" 외 {rdsBad.Count - 5}" : ""));

        var rdsHot = s.RdsServers.Where(r => r.Enabled != false && r.LoadIndex is int li && li >= t.RdsLoadWarn)
            .Select(r => $"{r.Name}({r.LoadIndex}%)").ToList();
        if (rdsHot.Count > 0) reasons.Add("세션호스트 고부하 " + string.Join(", ", rdsHot.Take(5)));

        // 이벤트 DB(미구성은 경고 아님 — 상세 탭에 표시만)
        if (s.EventDb != null && !Healthy.EventDb(s.EventDb.Status) &&
            !string.Equals(s.EventDb.Status, "NOT_CONFIGURED", StringComparison.OrdinalIgnoreCase))
            reasons.Add($"이벤트DB {s.EventDb.Status}");

        // AD 도메인
        var adBad = s.AdDomains.Where(a => !Healthy.Ad(a.Status)).Select(a => $"{a.DnsName}({a.Status})").ToList();
        if (adBad.Count > 0) reasons.Add("AD 이상 " + string.Join(", ", adBad));

        // vCenter
        var vcBad = s.VirtualCenters.Where(v => !Healthy.Vc(v.Status)).Select(v => $"{v.Name}({v.Status})").ToList();
        if (vcBad.Count > 0) reasons.Add("vCenter 이상 " + string.Join(", ", vcBad));

        // SAML 인증기
        var samlBad = s.SamlAuthenticators.Where(v => !Healthy.Saml(v.Status)).Select(v => $"{v.Label}({v.Status})").ToList();
        if (samlBad.Count > 0) reasons.Add("SAML 이상 " + string.Join(", ", samlBad));

        // 문제 머신
        if (s.ProblemMachines.Count > 0) reasons.Add($"문제 머신 {s.ProblemMachines.Count}대");

        // 부분 수집 실패
        if (s.EndpointErrors.Count > 0) reasons.Add("일부 수집 실패(" + string.Join("; ", s.EndpointErrors.Take(3)) + ")");

        if (reasons.Count > 0)
        {
            s.Status = HealthStatus.Warn;
            s.Error = string.Join(" / ", reasons);
        }
        else
        {
            s.Status = HealthStatus.Up;
            s.Error = null;
        }
    }

    /// <summary>경고 대상 세션호스트: 운영자가 의도적으로 끈 호스트(Enabled=false, 상태 DISABLED)는 제외.</summary>
    public static bool RdsProblem(RdsServerInfo r)
        => r.Enabled != false
           && !string.Equals(r.Status, "DISABLED", StringComparison.OrdinalIgnoreCase)
           && !Healthy.Rds(r);
}
