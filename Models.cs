using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace HorizonServiceMonitor;

/// <summary>법인(Horizon 서비스) 상태 등급.</summary>
public enum HealthStatus
{
    Unknown = 0, // 아직 점검 전
    Up = 1,      // 모든 구성요소 정상
    Warn = 2,    // 일부 구성요소 이상/주의(서비스·게이트웨이·세션호스트·인증서 등)
    Down = 3,    // API 미도달/로그인 실패/커넥션서버 전멸
}

/// <summary>모니터링 대상 법인(Horizon Pod — 커넥션 서버) 정의.</summary>
public sealed class Corporation
{
    public long Id { get; set; }
    public string Name { get; set; } = "";       // 법인명(표시용)
    public string Code { get; set; } = "";       // 법인 코드(짧은 식별자)
    public string Region { get; set; } = "";     // 리전/지역(표시·그룹핑용)
    public string ServerUrl { get; set; } = "";  // 커넥션서버(또는 LB) URL. 예: https://cs.corp.example.com
    public string Domain { get; set; } = "";     // AD 도메인(로그인용)
    public string Username { get; set; } = "";   // 모니터링 계정(읽기 전용 권한 권장)
    public string PasswordEnc { get; set; } = ""; // DPAPI(CurrentUser)로 암호화된 비밀번호(base64)
    public int IntervalSec { get; set; } = 60;
    public int TimeoutMs { get; set; } = 15000;  // 고RTT 사이트(800ms+)를 고려한 요청 타임아웃
    public bool Enabled { get; set; } = true;
    public int Sort { get; set; } = 0;
    // 머신/세션 인벤토리 상세 수집 여부(대규모 법인은 부하 고려해 끌 수 있음). N주기마다 1회 수집.
    public bool CollectInventory { get; set; } = true;
    public string Notes { get; set; } = "";

    [JsonIgnore]
    public string BaseUrl
    {
        get
        {
            var u = (ServerUrl ?? "").Trim().TrimEnd('/');
            if (u.Length == 0) return "";
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                u = "https://" + u;
            return u;
        }
    }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Code) ? Name : $"[{Code}] {Name}";
}

/// <summary>커넥션서버 내부 개별 서비스(Framework/MessageBus/SecurityGateway 등) 상태.</summary>
public sealed class ServiceInfo
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = ""; // UP / DOWN / UNKNOWN 등(API enum 원문 보존)
}

/// <summary>커넥션서버 1대 상태.</summary>
public sealed class ConnectionServerInfo
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";       // OK / ERROR / NOT_RESPONDING / UNKNOWN
    public string? Version { get; set; }
    public string? Build { get; set; }
    public int? ConnectionCount { get; set; }       // 이 CS를 통해 붙은 활성 연결 수
    public int? TunnelConnectionCount { get; set; }
    public bool? CertValid { get; set; }
    public DateTime? CertExpiryUtc { get; set; }
    public string? Replication { get; set; }        // LDAP 복제 상태 요약(OK/ERROR ...)
    public List<ServiceInfo> Services { get; set; } = new();
    public string? Details { get; set; }
}

/// <summary>게이트웨이(UAG/보안서버) 상태.</summary>
public sealed class GatewayInfo
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";  // OK / PROBLEM / NOT_CONTACTED / UNKNOWN / STALE
    public string? Type { get; set; }         // UAG / SECURITY_SERVER 등
    public string? Version { get; set; }
    public int? ActiveConnections { get; set; }
    public int? BlastCount { get; set; }
    public int? PcoipCount { get; set; }
    public bool? Internal { get; set; }
}

/// <summary>팜(RDS 세션호스트 그룹) 상태.</summary>
public sealed class FarmInfo
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";  // OK / WARNING / ERROR / DISABLED
    public string? Type { get; set; }         // AUTOMATED / MANUAL
    public int? RdsServerCount { get; set; }
    public int? ApplicationCount { get; set; }
}

/// <summary>세션호스트(RDS 서버) 1대 상태.</summary>
public sealed class RdsServerInfo
{
    public string Name { get; set; } = "";
    public string? FarmId { get; set; }
    public string? FarmName { get; set; }
    public string Status { get; set; } = "";  // OK / WARNING / ERROR / DISABLED ...
    public string? State { get; set; }        // AVAILABLE / UNREACHABLE / DISABLED ... (버전에 따라)
    public bool? Enabled { get; set; }
    public int? SessionCount { get; set; }
    public int? MaxSessions { get; set; }
    public int? LoadIndex { get; set; }       // 0~100 부하 지수
    public string? LoadPreference { get; set; }
    public string? AgentVersion { get; set; }
    public string? OperatingSystem { get; set; }
    public string? Details { get; set; }
}

/// <summary>AD 도메인 연동 상태.</summary>
public sealed class AdDomainInfo
{
    public string DnsName { get; set; } = "";
    public string? NetbiosName { get; set; }
    public string Status { get; set; } = "";           // FULLY_ACCESSIBLE / CONTACTABLE / UNCONTACTABLE ...
    public string? TrustRelationship { get; set; }
}

/// <summary>Horizon에 등록된 vCenter 상태.</summary>
public sealed class VirtualCenterInfo
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";  // OK / DOWN / RECONNECTING / UNKNOWN ...
    public string? Version { get; set; }
    public int? DatastoreTotal { get; set; }
    public int? DatastoreWarn { get; set; }   // 임계 상태(경고/에러) 데이터스토어 수
    public string? Details { get; set; }
}

/// <summary>SAML 인증기 상태.</summary>
public sealed class SamlInfo
{
    public string Label { get; set; } = "";
    public string Status { get; set; } = ""; // OK / ERROR / UNKNOWN
}

/// <summary>이벤트 데이터베이스 상태.</summary>
public sealed class EventDbInfo
{
    public string Status { get; set; } = ""; // CONNECTED / ERROR / DISCONNECTED / NOT_CONFIGURED ...
    public string? ServerName { get; set; }
    public long? EventCount { get; set; }
    public string? Details { get; set; }
}

/// <summary>데스크톱 풀 요약(모니터 헬스 + 인벤토리 병합).</summary>
public sealed class PoolInfo
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Status { get; set; } // OK / WARNING / ERROR / DISABLED (monitor/desktops, 미지원 버전이면 null)
    public bool? Enabled { get; set; }
    public string? Type { get; set; }    // AUTOMATED / MANUAL / RDS
    public string? Source { get; set; }  // INSTANT_CLONE / LINKED_CLONE / RDS / UNMANAGED ...
    public bool? ProvisioningEnabled { get; set; }
    public int? MachineCount { get; set; }
    public int? ProblemMachineCount { get; set; }
}

/// <summary>세션 통계 요약(monitor/sessions/metrics 또는 인벤토리 세션 집계).</summary>
public sealed class SessionSummary
{
    public int Total { get; set; }
    public int Connected { get; set; }
    public int Idle { get; set; }       // Connected에 포함(유휴)
    public int Disconnected { get; set; }
    public int Pending { get; set; }
    public int Other { get; set; }
    public int Desktop { get; set; }
    public int Application { get; set; }
    public int? Users { get; set; }     // 동시 사용자 수(메트릭 API 제공 시)
    public bool Truncated { get; set; } // 페이지 상한 도달로 일부만 집계된 경우
}

/// <summary>문제 상태 머신(에이전트 미도달/에러 등) 1건.</summary>
public sealed class MachineProblem
{
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public string? Pool { get; set; }
}

/// <summary>법인 1회 점검 결과(시계열 1 샘플 + 상세 스냅샷).</summary>
public sealed class CorpSnapshot
{
    public long CorpId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public HealthStatus Status { get; set; }
    public string? Error { get; set; }        // Down 사유 또는 Warn 사유 요약
    public double DurationMs { get; set; }    // 전체 수집 소요시간
    public int? ApiCertExpiryDays { get; set; } // 접속 URL TLS 인증서 만료까지 남은 일수

    public List<ConnectionServerInfo> ConnectionServers { get; set; } = new();
    public List<GatewayInfo> Gateways { get; set; } = new();
    public List<FarmInfo> Farms { get; set; } = new();
    public List<RdsServerInfo> RdsServers { get; set; } = new();
    public List<AdDomainInfo> AdDomains { get; set; } = new();
    public List<VirtualCenterInfo> VirtualCenters { get; set; } = new();
    public List<SamlInfo> SamlAuthenticators { get; set; } = new();
    public EventDbInfo? EventDb { get; set; }
    public List<PoolInfo> DesktopPools { get; set; } = new();
    public SessionSummary? Sessions { get; set; }
    public int? MachineTotal { get; set; }
    public List<MachineProblem> ProblemMachines { get; set; } = new();
    /// <summary>엔드포인트별 부분 실패("gateways: 404 미지원" 등) — 전체 Down은 아님.</summary>
    public List<string> EndpointErrors { get; set; } = new();

    // ── 파생 요약(카드/표 표시용) ─────────────────────────────────────────
    [JsonIgnore] public int CsTotal => ConnectionServers.Count;
    [JsonIgnore] public int CsOk => ConnectionServers.Count(c => Healthy.Cs(c.Status));
    [JsonIgnore] public int GwTotal => Gateways.Count;
    [JsonIgnore] public int GwOk => Gateways.Count(g => Healthy.Gateway(g.Status));
    [JsonIgnore] public int RdsTotal => RdsServers.Count;
    [JsonIgnore] public int RdsOk => RdsServers.Count(r => Healthy.Rds(r));
    [JsonIgnore] public int FarmTotal => Farms.Count;
    [JsonIgnore] public int FarmOk => Farms.Count(f => Healthy.Farm(f.Status));

    /// <summary>총 세션 수 — 인벤토리 집계가 있으면 그 값, 없으면 세션호스트 세션수 합→CS 연결수 합 순으로 폴백.</summary>
    [JsonIgnore]
    public int SessionTotal
    {
        get
        {
            if (Sessions != null) return Sessions.Total;
            if (RdsServers.Any(r => r.SessionCount.HasValue))
                return RdsServers.Sum(r => r.SessionCount ?? 0);
            return ConnectionServers.Sum(c => c.ConnectionCount ?? 0);
        }
    }
}

/// <summary>구성요소별 '정상' 판정 규칙 — API 원문 enum을 보존하되 판정만 통일한다.</summary>
public static class Healthy
{
    public static bool Cs(string status) => Eq(status, "OK");
    public static bool CsService(string status) => Eq(status, "UP") || Eq(status, "OK");
    public static bool Gateway(string status) => Eq(status, "OK") || Eq(status, "UP");
    public static bool Farm(string status) => Eq(status, "OK");
    public static bool Ad(string status) => Eq(status, "FULLY_ACCESSIBLE") || Eq(status, "OK");
    public static bool Vc(string status) => Eq(status, "OK");
    public static bool Saml(string status) => Eq(status, "OK");
    public static bool EventDb(string status) => Eq(status, "CONNECTED") || Eq(status, "OK");

    /// <summary>세션호스트 '정상' 판정(카드 비율용): 활성 + 상태 OK.</summary>
    public static bool Rds(RdsServerInfo r)
    {
        if (r.Enabled == false) return false;
        return Eq(r.Status, "OK") || Eq(r.Status, "AVAILABLE");
    }

    /// <summary>풀 헬스(monitor/desktops): DISABLED(운영자 의도)는 경고로 치지 않는다.</summary>
    public static bool PoolProblem(PoolInfo p)
        => p.Status != null && !Eq(p.Status, "OK") && !Eq(p.Status, "DISABLED");

    /// <summary>문제로 간주하는 머신 상태(운영 개입 필요 가능성).
    /// 스펙의 AGENT_ERROR_*(머신)와 AGENT_ERR_*(RDS 상태) 표기를 모두 수용.
    /// 드레인 모드/유지보수는 운영자 의도이므로 제외.</summary>
    public static bool MachineProblemState(string state)
    {
        var s = state.ToUpperInvariant();
        if (s.StartsWith("AGENT_ERROR_") || s.StartsWith("AGENT_ERR_")) return true;
        return s switch
        {
            "ERROR" or "AGENT_UNREACHABLE" or "AGENT_CONFIG_ERROR" or "ALREADY_USED"
                or "PROVISIONING_ERROR" or "UNKNOWN" => true,
            _ => false,
        };
    }

    private static bool Eq(string? a, string b) => string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>법인별 최신 상태 요약(그리드/트레이 표시용).</summary>
public sealed class CorpStatus
{
    public Corporation Corp { get; set; } = new();
    public CorpSnapshot? Latest { get; set; }
    public HealthStatus Status => Latest?.Status ?? HealthStatus.Unknown;
}
