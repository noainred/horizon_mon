using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonServiceMonitor;

/// <summary>온디맨드 세션 목록 1행(상세 탭용).</summary>
public sealed class SessionRow
{
    public string User { get; set; } = "";
    public string Machine { get; set; } = "";
    public string PoolOrFarm { get; set; } = "";
    public string Type { get; set; } = "";      // DESKTOP / APPLICATION
    public string State { get; set; } = "";     // CONNECTED / DISCONNECTED / PENDING
    public DateTime? StartUtc { get; set; }
    public string? Protocol { get; set; }
    public string? ClientAddress { get; set; }
    public string? ClientType { get; set; }
    public string? ClientVersion { get; set; }
    public string? AgentVersion { get; set; }
}

/// <summary>온디맨드 머신 목록 1행(상세 탭용).</summary>
public sealed class MachineRow
{
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public string Pool { get; set; } = "";
    public string? AgentVersion { get; set; }
    public string? OperatingSystem { get; set; }
}

/// <summary>
/// Horizon Connection Server REST API 클라이언트(법인 1개 담당).
/// - POST /rest/login (domain/username/password) → Bearer 토큰. 401 시 /rest/refresh → 재로그인 폴백.
/// - 모니터 엔드포인트는 최신 버전부터(v3→v2→v1) 시도하고 성공한 버전을 기억한다.
///   선택적 엔드포인트(sessions/metrics, desktops 등)는 미지원(404)이면 조용히 건너뛴다(구버전 호환).
/// - 응답 파싱은 방어적으로(필드 후보 다중, details 하위 폴백) — 버전별 스키마 차이를 흡수한다.
///   (공식 스펙 기준: CS 버전/빌드는 details 하위, 인증서 만료는 valid_to, vCenter/SAML 상태는
///    connection_servers[] 원소별 → 최악값으로 집계, 페이지네이션 헤더는 HAS_MORE_RECORDS.)
/// - 사설/자체 서명 인증서가 흔하므로 TLS 신뢰 검증은 통과시키되 만료일만 기록한다(모니터링 목적).
/// FetchSnapshotAsync는 예외를 던지지 않고 실패를 스냅샷(Error/EndpointErrors)으로 환원한다.
/// </summary>
public sealed class HorizonClient : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _authSem = new(1, 1); // 로그인/갱신 직렬화(병렬 태스크 로그인 폭주 방지)
    private HttpClient? _http;
    private bool _disposed;
    private string _identity = "";           // BaseUrl+Domain+User+PwEnc — 바뀌면 세션 재구성
    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _tokenAtUtc;            // 발급 시각(선제 갱신용)
    private DateTime? _certNotAfterUtc;      // TLS 콜백에서 캡처한 서버 인증서 만료
    private readonly ConcurrentDictionary<string, string> _resolvedVersion = new();   // endpoint → 성공한 버전
    private readonly ConcurrentDictionary<string, DateTime> _unsupported = new();     // 미지원(404) 선택적 엔드포인트 → 기록 시각
    private readonly ConcurrentDictionary<string, string> _userNameCache = new();     // user_id → 표시명

    private const int TokenRefreshAfterMin = 20; // access token 선제 갱신(기본 만료 30분)
    private static readonly TimeSpan UnsupportedRetryAfter = TimeSpan.FromHours(6); // 포드 업그레이드 대비 재시도 주기

    // ── HTTP 준비 ────────────────────────────────────────────────────────────
    private HttpClient GetHttp(Corporation corp)
    {
        var id = corp.BaseUrl + "\n" + corp.Domain + "\n" + corp.Username + "\n" + corp.PasswordEnc;
        lock (_gate)
        {
            // 리셋(설정 변경/삭제)으로 파기된 클라이언트를 되살리지 않는다 —
            // in-flight 요청은 여기서 던지고 Guard/호출부가 결과를 폐기한다.
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_http != null && _identity == id) return _http;
            _http?.Dispose();
            var handler = new HttpClientHandler
            {
                CheckCertificateRevocationList = false,
                AllowAutoRedirect = false,
                // 사설/자체 서명 인증서 허용(도달성/상태 모니터링 목적). 만료일만 캡처해 경고에 사용.
                ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                {
                    if (cert != null) { try { _certNotAfterUtc = cert.NotAfter.ToUniversalTime(); } catch { /* ignore */ } }
                    return true;
                },
            };
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; // 요청별 CTS로 제어
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("HorizonServiceMonitor/1.0");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            _identity = id;
            _accessToken = null;
            _refreshToken = null;
            _resolvedVersion.Clear();
            _unsupported.Clear();
            return _http;
        }
    }

    private CancellationTokenSource Linked(Corporation corp, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Math.Max(2000, corp.TimeoutMs));
        return cts;
    }

    // ── 인증 ─────────────────────────────────────────────────────────────────
    private async Task LoginAsync(Corporation corp, CancellationToken ct)
    {
        var http = GetHttp(corp);
        var pw = Crypto.Unprotect(corp.PasswordEnc);
        if (string.IsNullOrEmpty(pw))
            throw new InvalidOperationException("비밀번호가 없거나 복호화 실패(설정에서 재입력 필요)");
        var body = JsonSerializer.Serialize(new { domain = corp.Domain, username = corp.Username, password = pw });
        using var cts = Linked(corp, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, corp.BaseUrl + "/rest/login")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            throw new InvalidOperationException("로그인 거부(HTTP " + (int)resp.StatusCode + "): 계정/도메인/비밀번호 확인" + ApiErrorSuffix(text));
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                $"로그인 실패(HTTP {(int)resp.StatusCode}): /rest 미노출 — UAG 주소라면 UAG 관리 UI(9443) › Horizon Settings › " +
                "Proxy Pattern에 |/rest(.*) 추가 필요" + ApiErrorSuffix(text));
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"로그인 실패(HTTP {(int)resp.StatusCode})" + ApiErrorSuffix(text));
        using var doc = JsonDocument.Parse(text);
        _accessToken = J.Str(doc.RootElement, "access_token");
        _refreshToken = J.Str(doc.RootElement, "refresh_token");
        _tokenAtUtc = DateTime.UtcNow;
        if (string.IsNullOrEmpty(_accessToken))
            throw new InvalidOperationException("로그인 응답에 access_token이 없음(Horizon REST API 미지원 버전?)");
    }

    private async Task<bool> TryRefreshAsync(Corporation corp, CancellationToken ct)
    {
        var rt = _refreshToken;
        if (string.IsNullOrEmpty(rt)) return false;
        try
        {
            var http = GetHttp(corp);
            using var cts = Linked(corp, ct);
            using var req = new HttpRequestMessage(HttpMethod.Post, corp.BaseUrl + "/rest/refresh")
            { Content = new StringContent(JsonSerializer.Serialize(new { refresh_token = rt }), Encoding.UTF8, "application/json") };
            using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var tok = J.Str(doc.RootElement, "access_token");
            if (string.IsNullOrEmpty(tok)) return false;
            _accessToken = tok;
            _tokenAtUtc = DateTime.UtcNow;
            return true;
        }
        catch { return false; }
    }

    private async Task EnsureAuthAsync(Corporation corp, CancellationToken ct)
    {
        GetHttp(corp); // identity 변화 감지(토큰 무효화)
        if (_accessToken != null && (DateTime.UtcNow - _tokenAtUtc).TotalMinutes < TokenRefreshAfterMin) return;
        await _authSem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 대기 중 다른 태스크가 이미 갱신했으면 종료(로그인 폭주 방지).
            if (_accessToken != null && (DateTime.UtcNow - _tokenAtUtc).TotalMinutes < TokenRefreshAfterMin) return;
            if (_accessToken != null && await TryRefreshAsync(corp, ct).ConfigureAwait(false)) return;
            await LoginAsync(corp, ct).ConfigureAwait(false);
        }
        finally { _authSem.Release(); }
    }

    /// <summary>401 수신 후 재인증 — usedToken이 이미 교체돼 있으면 갱신 생략(중복 로그인 방지),
    /// 아니면 refresh → 실패 시 재로그인.</summary>
    private async Task ReauthAsync(Corporation corp, string? usedToken, CancellationToken ct)
    {
        await _authSem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accessToken != null && _accessToken != usedToken) return; // 다른 태스크가 이미 갱신
            if (await TryRefreshAsync(corp, ct).ConfigureAwait(false)) return;
            await LoginAsync(corp, ct).ConfigureAwait(false);
        }
        finally { _authSem.Release(); }
    }

    private static string ApiErrorSuffix(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            var m = J.Str(root, "error_message", "message", "error_key");
            return string.IsNullOrEmpty(m) ? "" : " — " + m;
        }
        catch { return ""; }
    }

    /// <summary>인증 포함 GET. 401이면 refresh→재로그인 후 1회 재시도. (path는 /rest/... 전체 경로)</summary>
    private async Task<(HttpStatusCode Code, string Body, bool HasMore)> GetRawAsync(Corporation corp, string path, CancellationToken ct)
    {
        await EnsureAuthAsync(corp, ct).ConfigureAwait(false);
        for (int attempt = 0; ; attempt++)
        {
            var http = GetHttp(corp);
            var token = _accessToken; // 지역 복사 — 병렬 태스크의 토큰 교체와 무관하게 일관된 헤더 사용
            using var cts = Linked(corp, ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, corp.BaseUrl + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                await ReauthAsync(corp, token, ct).ConfigureAwait(false); // 만료 → 갱신/재로그인 후 재시도
                continue;
            }
            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            // 공식 스펙 헤더는 HAS_MORE_RECORDS(밑줄) — 방어적으로 하이픈 표기도 함께 확인.
            bool hasMore =
                (resp.Headers.TryGetValues("HAS_MORE_RECORDS", out var v1) && v1.Any(IsTrue)) ||
                (resp.Headers.TryGetValues("HAS-MORE-RECORDS", out var v2) && v2.Any(IsTrue));
            return (resp.StatusCode, body, hasMore);

            static bool IsTrue(string x) => string.Equals(x, "TRUE", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>모니터 엔드포인트 GET — 성공한 API 버전을 기억해 다음부터 바로 사용. 404/400만 버전 폴백.</summary>
    private async Task<JsonDocument> GetMonitorAsync(Corporation corp, string endpoint, CancellationToken ct, params string[] versions)
    {
        if (versions.Length == 0) versions = new[] { "v2", "v1" };
        if (_resolvedVersion.TryGetValue(endpoint, out var known))
        {
            var r = await GetRawAsync(corp, $"/rest/monitor/{known}/{endpoint}", ct).ConfigureAwait(false);
            if (r.Code == HttpStatusCode.OK) return JsonDocument.Parse(r.Body);
            _resolvedVersion.TryRemove(endpoint, out _); // 업그레이드 등으로 바뀜 → 재탐색
        }
        HttpStatusCode last = 0;
        string lastBody = "";
        foreach (var ver in versions)
        {
            var r = await GetRawAsync(corp, $"/rest/monitor/{ver}/{endpoint}", ct).ConfigureAwait(false);
            if (r.Code == HttpStatusCode.OK)
            {
                _resolvedVersion[endpoint] = ver;
                return JsonDocument.Parse(r.Body);
            }
            last = r.Code; lastBody = r.Body;
            if (r.Code is not (HttpStatusCode.NotFound or HttpStatusCode.BadRequest)) break;
        }
        throw new HttpRequestException($"{endpoint}: HTTP {(int)last}{ApiErrorSuffix(lastBody)}", null, last);
    }

    /// <summary>인벤토리 목록 GET(페이지네이션 + 버전 폴백) — rows 상한까지 page 루프. size 최대 1000(스펙).
    /// Truncated=true면 상한 도달로 서버에 더 많은 행이 남아 있다.</summary>
    private async Task<(List<JsonElement> Items, bool Truncated)> GetInventoryListAsync(
        Corporation corp, string endpoint, int maxRows, CancellationToken ct, params string[] versions)
    {
        if (versions.Length == 0) versions = new[] { "v1" };
        var cacheKey = "inv:" + endpoint;
        string ver = _resolvedVersion.TryGetValue(cacheKey, out var known) ? known : versions[0];

        var items = new List<JsonElement>();
        bool hasMore = false;
        int page = 1;
        const int size = 1000;
        while (items.Count < maxRows)
        {
            var r = await GetRawAsync(corp, $"/rest/inventory/{ver}/{endpoint}?page={page}&size={size}", ct).ConfigureAwait(false);
            if (r.Code is HttpStatusCode.NotFound or HttpStatusCode.BadRequest && page == 1)
            {
                // 버전 폴백(v2 미지원 구버전 등) — 다음 후보 시도.
                var idx = Array.IndexOf(versions, ver);
                if (idx >= 0 && idx + 1 < versions.Length)
                {
                    ver = versions[idx + 1];
                    _resolvedVersion.TryRemove(cacheKey, out _);
                    continue;
                }
            }
            if (r.Code != HttpStatusCode.OK)
                throw new HttpRequestException($"{endpoint}: HTTP {(int)r.Code}{ApiErrorSuffix(r.Body)}", null, r.Code);
            _resolvedVersion[cacheKey] = ver;
            using var doc = JsonDocument.Parse(r.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) break;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                items.Add(el.Clone());
                if (items.Count >= maxRows) break;
            }
            hasMore = r.HasMore;
            if (!r.HasMore || doc.RootElement.GetArrayLength() == 0) break;
            page++;
        }
        return (items, items.Count >= maxRows && hasMore);
    }

    // ── 스냅샷 수집 ──────────────────────────────────────────────────────────
    /// <summary>법인 1회 수집. 예외를 던지지 않는다(실패는 Error/EndpointErrors로 환원).</summary>
    public async Task<CorpSnapshot> FetchSnapshotAsync(Corporation corp, bool includeInventory, CancellationToken ct)
    {
        var snap = new CorpSnapshot { CorpId = corp.Id, TimestampUtc = DateTime.UtcNow };
        var sw = Stopwatch.StartNew();
        try
        {
            await EnsureAuthAsync(corp, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            snap.DurationMs = sw.Elapsed.TotalMilliseconds;
            snap.Status = HealthStatus.Down;
            snap.Error = Short(ex);
            SetApiCertDays(snap);
            return snap;
        }

        // 모니터 엔드포인트 병렬 수집(엔드포인트별 실패 격리).
        var tasks = new List<Task>
        {
            Guard(snap, "connection-servers", async () =>
            {
                using var doc = await GetMonitorAsync(corp, "connection-servers", ct, "v3", "v2", "v1").ConfigureAwait(false);
                snap.ConnectionServers = ParseConnectionServers(doc.RootElement);
            }),
            Guard(snap, "gateways", async () =>
            {
                using var doc = await GetMonitorAsync(corp, "gateways", ct, "v3", "v2", "v1").ConfigureAwait(false);
                snap.Gateways = ParseGateways(doc.RootElement);
            }),
            Guard(snap, "farms", async () =>
            {
                using var doc = await GetMonitorAsync(corp, "farms", ct, "v1").ConfigureAwait(false);
                snap.Farms = ParseFarms(doc.RootElement);
            }),
            Guard(snap, "rds-servers", async () =>
            {
                using var doc = await GetMonitorAsync(corp, "rds-servers", ct, "v1").ConfigureAwait(false);
                snap.RdsServers = ParseRdsServers(doc.RootElement);
            }),
            Guard(snap, "event-database", async () =>
            {
                using var doc = await GetMonitorAsync(corp, "event-database", ct, "v1").ConfigureAwait(false);
                snap.EventDb = ParseEventDb(doc.RootElement);
            }),
            Guard(snap, "ad-domains", async () =>
            {
                using var doc = await GetMonitorAsync(corp, "ad-domains", ct, "v2", "v1").ConfigureAwait(false);
                snap.AdDomains = ParseAdDomains(doc.RootElement);
            }),
            Guard(snap, "virtual-centers", async () =>
            {
                using var doc = await GetMonitorAsync(corp, "virtual-centers", ct, "v3", "v2", "v1").ConfigureAwait(false);
                snap.VirtualCenters = ParseVirtualCenters(doc.RootElement);
            }),
            Guard(snap, "saml-authenticators", async () =>
            {
                using var doc = await GetMonitorAsync(corp, "saml-authenticators", ct, "v2", "v1").ConfigureAwait(false);
                snap.SamlAuthenticators = ParseSaml(doc.RootElement);
            }),
            // 선택적(2012+): 풀 헬스 — 미지원 버전이면 조용히 생략.
            GuardOptional(snap, "desktops", async () =>
            {
                using var doc = await GetMonitorAsync(corp, "desktops", ct, "v1").ConfigureAwait(false);
                snap.DesktopPools = ParseMonitorDesktops(doc.RootElement);
            }),
            // 선택적(2309+): 경량 세션 집계 — 매 주기 세션 요약을 싸게 얻는다.
            GuardOptional(snap, "sessions/metrics", async () =>
            {
                using var doc = await GetMonitorAsync(corp, "sessions/metrics", ct, "v1").ConfigureAwait(false);
                snap.Sessions = ParseSessionMetrics(doc.RootElement);
            }),
        };
        await Task.WhenAll(tasks).ConfigureAwait(false);

        // 팜 이름 매핑(세션호스트 farm_id → 이름).
        var farmNames = snap.Farms.Where(f => f.Id != null).ToDictionary(f => f.Id!, f => f.Name);
        foreach (var r in snap.RdsServers)
            if (r.FarmId != null && farmNames.TryGetValue(r.FarmId, out var fn)) r.FarmName = fn;

        if (includeInventory)
        {
            await Guard(snap, "desktop-pools", async () =>
            {
                // enable_provisioning은 v2+에만 존재 — v2 우선, 구버전이면 v1 폴백.
                var (pools, _) = await GetInventoryListAsync(corp, "desktop-pools", 500, ct, "v2", "v1").ConfigureAwait(false);
                MergeInventoryPools(snap, pools);
            }).ConfigureAwait(false);

            await Guard(snap, "machines", async () =>
            {
                var (machines, truncated) = await GetInventoryListAsync(corp, "machines", 10000, ct).ConfigureAwait(false);
                snap.MachineTotal = machines.Count;
                snap.MachinesTruncated = truncated;
                var poolNames = snap.DesktopPools.Where(p => p.Id != null).ToDictionary(p => p.Id!, p => p.Name);
                var problems = new List<MachineProblem>();
                var problemByPool = new Dictionary<string, int>();
                var countByPool = new Dictionary<string, int>();
                foreach (var m in machines)
                {
                    var poolId = J.Str(m, "desktop_pool_id");
                    if (poolId != null) countByPool[poolId] = countByPool.GetValueOrDefault(poolId) + 1;
                    var state = J.Str(m, "state") ?? "";
                    if (!Healthy.MachineProblemState(state)) continue;
                    var pool = poolId != null && poolNames.TryGetValue(poolId, out var pn) ? pn : poolId ?? "";
                    problems.Add(new MachineProblem { Name = J.Str(m, "name") ?? "?", State = state, Pool = pool });
                    if (poolId != null) problemByPool[poolId] = problemByPool.GetValueOrDefault(poolId) + 1;
                }
                snap.ProblemMachines = problems;
                foreach (var p in snap.DesktopPools)
                {
                    if (p.Id == null) continue;
                    if (problemByPool.TryGetValue(p.Id, out var n)) p.ProblemMachineCount = n;
                    if (countByPool.TryGetValue(p.Id, out var t)) p.MachineCount = t;
                }
            }).ConfigureAwait(false);

            // 세션 요약: 경량 메트릭이 없었던(구버전) 경우에만 인벤토리로 집계.
            if (snap.Sessions == null)
            {
                await Guard(snap, "sessions", async () =>
                {
                    const int cap = 20000;
                    var (sessions, truncated) = await GetInventoryListAsync(corp, "sessions", cap, ct).ConfigureAwait(false);
                    var sum = new SessionSummary { Total = sessions.Count, Truncated = truncated };
                    foreach (var s in sessions)
                    {
                        var state = (J.Str(s, "session_state") ?? "").ToUpperInvariant();
                        switch (state)
                        {
                            case "CONNECTED": sum.Connected++; break;
                            case "DISCONNECTED": sum.Disconnected++; break;
                            case "PENDING": sum.Pending++; break;
                            default: sum.Other++; break;
                        }
                        var type = (J.Str(s, "session_type") ?? "").ToUpperInvariant();
                        if (type == "APPLICATION") sum.Application++; else sum.Desktop++;
                    }
                    snap.Sessions = sum;
                }).ConfigureAwait(false);
            }
        }

        sw.Stop();
        snap.DurationMs = sw.Elapsed.TotalMilliseconds;
        SetApiCertDays(snap);
        // Down 판정(Error 설정)은 '모든' 모니터 엔드포인트가 실패해 데이터가 전무할 때만.
        // connection-servers 1개만 일시 실패한 경우는 EndpointErrors 기반 Warn으로 처리한다.
        var anyData = snap.ConnectionServers.Count > 0 || snap.Gateways.Count > 0 || snap.Farms.Count > 0 ||
                      snap.RdsServers.Count > 0 || snap.AdDomains.Count > 0 || snap.VirtualCenters.Count > 0 ||
                      snap.SamlAuthenticators.Count > 0 || snap.EventDb != null;
        if (!anyData && snap.EndpointErrors.Count > 0 && snap.Error == null)
            snap.Error = "수집 실패: " + string.Join("; ", snap.EndpointErrors.Take(2));
        return snap;
    }

    private void SetApiCertDays(CorpSnapshot snap)
    {
        if (_certNotAfterUtc is DateTime na)
            snap.ApiCertExpiryDays = (int)Math.Floor((na - DateTime.UtcNow).TotalDays);
    }

    private static async Task Guard(CorpSnapshot snap, string name, Func<Task> work)
    {
        try { await work().ConfigureAwait(false); }
        catch (Exception ex)
        {
            lock (snap.EndpointErrors) snap.EndpointErrors.Add($"{name}: {Short(ex)}");
        }
    }

    /// <summary>선택적 엔드포인트 — 미지원(404/400)이면 기억하고 조용히 생략(경고 없음).
    /// 포드 업그레이드로 지원될 수 있으므로 일정 시간 후 재시도한다.</summary>
    private Task GuardOptional(CorpSnapshot snap, string name, Func<Task> work)
    {
        if (_unsupported.TryGetValue(name, out var at))
        {
            if (DateTime.UtcNow - at < UnsupportedRetryAfter) return Task.CompletedTask;
            _unsupported.TryRemove(name, out _);
        }
        return Run();
        async Task Run()
        {
            try { await work().ConfigureAwait(false); }
            catch (HttpRequestException hex) when (hex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                _unsupported[name] = DateTime.UtcNow; // 구버전 — 당분간 시도 안 함
            }
            catch (Exception ex)
            {
                lock (snap.EndpointErrors) snap.EndpointErrors.Add($"{name}: {Short(ex)}");
            }
        }
    }

    private static string Short(Exception ex)
    {
        var m = ex is OperationCanceledException ? "시간 초과" : (ex.InnerException?.Message ?? ex.Message);
        return m.Length > 160 ? m.Substring(0, 160) : m;
    }

    // ── 파싱(방어적 — 공식 스펙 + 버전별 차이 흡수) ──────────────────────────
    private static List<ConnectionServerInfo> ParseConnectionServers(JsonElement root)
    {
        var list = new List<ConnectionServerInfo>();
        if (root.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in root.EnumerateArray())
        {
            var details = J.Child(el, "details");
            var cs = new ConnectionServerInfo
            {
                Name = J.Str(el, "name") ?? "?",
                Status = J.Str(el, "status") ?? "UNKNOWN",
                // 스펙: version/build는 details 하위. (혹시 모를 변형 대비 top-level도 폴백)
                Version = (details is JsonElement d1 ? J.Str(d1, "version") : null) ?? J.Str(el, "version"),
                Build = (details is JsonElement d2 ? J.Str(d2, "build") : null) ?? J.Str(el, "build"),
                ConnectionCount = J.Int(el, "connection_count"),
                TunnelConnectionCount = J.Int(el, "tunnel_connection_count"),
            };
            if (J.Child(el, "certificate") is JsonElement cert)
            {
                cs.CertValid = J.Bool(cert, "valid");
                var until = J.Long(cert, "valid_to", "valid_until");
                if (until is long ms && ms > 0) cs.CertExpiryUtc = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            }
            if (J.Child(el, "services") is JsonElement svcs && svcs.ValueKind == JsonValueKind.Array)
                foreach (var sv in svcs.EnumerateArray())
                    cs.Services.Add(new ServiceInfo
                    {
                        Name = J.Str(sv, "service_name", "name") ?? "?",
                        Status = J.Str(sv, "status") ?? "UNKNOWN",
                    });
            if (J.Child(el, "cs_replications") is JsonElement reps && reps.ValueKind == JsonValueKind.Array)
            {
                var parts = reps.EnumerateArray()
                    .Select(rp => $"{J.Str(rp, "server_name", "name") ?? "?"}={J.Str(rp, "status") ?? "?"}")
                    .ToList();
                if (parts.Count > 0) cs.Replication = string.Join(", ", parts);
            }
            if (J.Child(el, "session_protocol_data") is JsonElement prot && prot.ValueKind == JsonValueKind.Array)
            {
                var parts = prot.EnumerateArray()
                    .Select(pp => $"{J.Str(pp, "session_protocol") ?? "?"} {J.Int(pp, "session_count") ?? 0}")
                    .ToList();
                if (parts.Count > 0) cs.Details = "프로토콜: " + string.Join(" · ", parts);
            }
            list.Add(cs);
        }
        return list;
    }

    private static List<GatewayInfo> ParseGateways(JsonElement root)
    {
        var list = new List<GatewayInfo>();
        if (root.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in root.EnumerateArray())
        {
            var details = J.Child(el, "details");
            list.Add(new GatewayInfo
            {
                Name = J.Str(el, "name") ?? "?",
                Status = J.Str(el, "status") ?? "UNKNOWN",
                Type = (details is JsonElement d1 ? J.Str(d1, "type") : null) ?? J.Str(el, "type"),
                Version = (details is JsonElement d2 ? J.Str(d2, "version") : null) ?? J.Str(el, "version"),
                Internal = (details is JsonElement d3 ? J.Bool(d3, "internal") : null) ?? J.Bool(el, "internal"),
                ActiveConnections = J.Int(el, "active_connection_count", "active_connections"),
                BlastCount = J.Int(el, "blast_connection_count"),
                PcoipCount = J.Int(el, "pcoip_connection_count"),
            });
        }
        return list;
    }

    private static List<FarmInfo> ParseFarms(JsonElement root)
    {
        var list = new List<FarmInfo>();
        if (root.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in root.EnumerateArray())
        {
            var details = J.Child(el, "details");
            list.Add(new FarmInfo
            {
                Id = J.Str(el, "id"),
                Name = J.Str(el, "name") ?? "?",
                Status = J.Str(el, "status") ?? "UNKNOWN",
                Type = (details is JsonElement d ? J.Str(d, "type") : null) ?? J.Str(el, "type"),
                RdsServerCount = J.Int(el, "rds_server_count"),
                ApplicationCount = J.Int(el, "application_count"),
            });
        }
        return list;
    }

    private static List<RdsServerInfo> ParseRdsServers(JsonElement root)
    {
        var list = new List<RdsServerInfo>();
        if (root.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in root.EnumerateArray())
        {
            var details = J.Child(el, "details");
            int? Num(params string[] names)
                => J.Int(el, names) ?? (details is JsonElement d ? J.Int(d, names) : null);
            string? Txt(params string[] names)
                => J.Str(el, names) ?? (details is JsonElement d ? J.Str(d, names) : null);
            var r = new RdsServerInfo
            {
                Name = J.Str(el, "name") ?? "?",
                FarmId = J.Str(el, "farm_id"),
                Status = J.Str(el, "status") ?? "UNKNOWN",
                State = Txt("state"),
                Enabled = J.Bool(el, "enabled") ?? (details is JsonElement d2 ? J.Bool(d2, "enabled") : null),
                SessionCount = Num("session_count"),
                MaxSessions = Num("max_sessions_count_configured", "max_sessions_count", "max_sessions"),
                LoadIndex = Num("load_index"),
                LoadPreference = Txt("load_preference"),
                AgentVersion = Txt("agent_version"),
                OperatingSystem = Txt("operating_system"),
            };
            list.Add(r);
        }
        return list;
    }

    private static EventDbInfo ParseEventDb(JsonElement root)
    {
        var details = J.Child(root, "details");
        return new EventDbInfo
        {
            Status = J.Str(root, "status") ?? "UNKNOWN",
            ServerName = details is JsonElement d ? J.Str(d, "server_name") : J.Str(root, "server_name"),
            EventCount = J.Long(root, "event_count"),
            Details = details is JsonElement d2
                ? string.Join(" · ", new[]
                    {
                        J.Str(d2, "database_name") is string db ? "DB " + db : null,
                        J.Int(d2, "port") is int p ? "포트 " + p : null,
                        J.Str(d2, "type") is string ty ? ty : null,
                        J.Str(d2, "user_name") is string u ? "계정 " + u : null,
                    }.Where(x => x != null))
                : null,
        };
    }

    /// <summary>connection_servers[] 원소들의 status를 최악값으로 집계(랭크 함수는 유형별 제공).</summary>
    private static (string Status, JsonElement? First) WorstOfConnectionServers(JsonElement el, Func<string, int> rank)
    {
        string worst = "";
        int worstRank = -1;
        JsonElement? first = null;
        if (J.Child(el, "connection_servers") is JsonElement css && css.ValueKind == JsonValueKind.Array)
        {
            foreach (var cs in css.EnumerateArray())
            {
                first ??= cs;
                var st = J.Str(cs, "status") ?? "UNKNOWN";
                var r = rank(st.ToUpperInvariant());
                if (r > worstRank) { worstRank = r; worst = st; }
            }
        }
        return (worst, first);
    }

    private static List<AdDomainInfo> ParseAdDomains(JsonElement root)
    {
        var list = new List<AdDomainInfo>();
        if (root.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in root.EnumerateArray())
        {
            var info = new AdDomainInfo
            {
                DnsName = J.Str(el, "dns_name") ?? "?",
                NetbiosName = J.Str(el, "netbios_name", "net_bios_name"),
                Status = J.Str(el, "status", "connection_status") ?? "",
                TrustRelationship = J.Str(el, "trust_relationship"),
            };
            if (info.Status.Length == 0)
            {
                var (worst, first) = WorstOfConnectionServers(el, st => st switch
                {
                    "FULLY_ACCESSIBLE" => 0, "CONTACTABLE" => 1, "CANNOT_BIND" => 2, "UNCONTACTABLE" => 3, _ => 2,
                });
                info.Status = worst.Length > 0 ? worst : "UNKNOWN";
                if (info.TrustRelationship == null && first is JsonElement f)
                    info.TrustRelationship = J.Str(f, "trust_relationship");
            }
            list.Add(info);
        }
        return list;
    }

    private static List<VirtualCenterInfo> ParseVirtualCenters(JsonElement root)
    {
        var list = new List<VirtualCenterInfo>();
        if (root.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in root.EnumerateArray())
        {
            var details = J.Child(el, "details");
            var vc = new VirtualCenterInfo
            {
                Name = J.Str(el, "name", "address", "url", "id") ?? "?",
                // 스펙: vCenter 상태는 connection_servers[] 원소별 → 최악값 집계. (top-level status 변형도 폴백)
                Status = J.Str(el, "status") ?? "",
                Version = (details is JsonElement d ? J.Str(d, "version") : null) ?? J.Str(el, "version"),
            };
            if (vc.Status.Length == 0)
            {
                var (worst, _) = WorstOfConnectionServers(el, st => st switch
                {
                    "OK" => 0, "RECONNECTING" => 1, "NOT_YET_CONNECTED" => 2,
                    "INVALID_CREDENTIALS" => 3, "CANNOT_LOGIN" => 3, "DOWN" => 4, _ => 2,
                });
                vc.Status = worst.Length > 0 ? worst : "UNKNOWN";
            }
            if (J.Child(el, "datastores") is JsonElement dss && dss.ValueKind == JsonValueKind.Array)
            {
                int total = 0, warn = 0;
                foreach (var ds in dss.EnumerateArray())
                {
                    total++;
                    var st = (J.Str(ds, "status") ?? "").ToUpperInvariant();
                    if (st.Length > 0 && st != "OK" && st != "ACCESSIBLE") warn++;
                }
                vc.DatastoreTotal = total;
                vc.DatastoreWarn = warn;
            }
            list.Add(vc);
        }
        return list;
    }

    private static List<SamlInfo> ParseSaml(JsonElement root)
    {
        var list = new List<SamlInfo>();
        if (root.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in root.EnumerateArray())
        {
            var details = J.Child(el, "details");
            var status = J.Str(el, "status") ?? "";
            if (status.Length == 0)
            {
                var (worst, _) = WorstOfConnectionServers(el, st => st switch
                {
                    "OK" => 0, "WARN" => 1, "UNKNOWN" => 2, "ERROR" => 3, _ => 2,
                });
                status = worst.Length > 0 ? worst : "UNKNOWN";
            }
            list.Add(new SamlInfo
            {
                Label = (details is JsonElement d ? J.Str(d, "label") : null) ?? J.Str(el, "label", "name", "id") ?? "?",
                Status = status,
            });
        }
        return list;
    }

    /// <summary>GET /rest/monitor/v1/desktops — 풀 헬스(매 주기, 저비용).</summary>
    private static List<PoolInfo> ParseMonitorDesktops(JsonElement root)
    {
        var list = new List<PoolInfo>();
        if (root.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in root.EnumerateArray())
        {
            list.Add(new PoolInfo
            {
                Id = J.Str(el, "id"),
                Name = J.Str(el, "name") ?? "?",
                Status = J.Str(el, "status"),
                Type = J.Str(el, "type"),
                Source = J.Str(el, "source"),
                Enabled = J.Bool(el, "enabled"),
            });
        }
        return list;
    }

    /// <summary>인벤토리 풀 정보를 모니터 풀 목록에 병합(표시명/브로커링·프로비저닝 여부 보강).</summary>
    private static void MergeInventoryPools(CorpSnapshot snap, List<JsonElement> pools)
    {
        var byId = snap.DesktopPools.Where(p => p.Id != null).ToDictionary(p => p.Id!);
        foreach (var el in pools)
        {
            var id = J.Str(el, "id");
            PoolInfo p;
            if (id != null && byId.TryGetValue(id, out var existing)) p = existing;
            else
            {
                p = new PoolInfo { Id = id, Name = J.Str(el, "name") ?? "?" };
                snap.DesktopPools.Add(p);
                if (id != null) byId[id] = p;
            }
            p.DisplayName = J.Str(el, "display_name") ?? p.DisplayName;
            p.Enabled = J.Bool(el, "enabled") ?? p.Enabled;
            p.Type ??= J.Str(el, "type");
            p.Source ??= J.Str(el, "source");
            p.ProvisioningEnabled = J.Bool(el, "enable_provisioning", "provisioning_enabled") ?? p.ProvisioningEnabled;
        }
    }

    /// <summary>GET /rest/monitor/v1/sessions/metrics — 경량 세션 집계(2309+).</summary>
    private static SessionSummary ParseSessionMetrics(JsonElement root)
    {
        var sum = new SessionSummary { Total = J.Int(root, "num_sessions") ?? 0, Users = J.Int(root, "num_users") };
        int desktopish = 0;
        foreach (var group in new[] { "desktop_session_metrics", "application_session_metrics", "rds_session_metrics" })
        {
            if (J.Child(root, group) is not JsonElement g) continue;
            var active = J.Int(g, "num_active_sessions") ?? 0;
            var idle = J.Int(g, "num_idle_sessions") ?? 0;
            var disc = J.Int(g, "num_disconnected_sessions") ?? 0;
            var pend = J.Int(g, "num_pending_sessions") ?? 0;
            sum.Connected += active + idle;
            sum.Idle += idle;
            sum.Disconnected += disc;
            sum.Pending += pend;
            var groupTotal = active + idle + disc + pend;
            if (group == "application_session_metrics") sum.Application += groupTotal;
            else desktopish += groupTotal;
        }
        sum.Desktop = desktopish;
        return sum;
    }

    // ── 온디맨드 상세 조회(상세 탭/연결 테스트) ─────────────────────────────
    /// <summary>연결 테스트 — 로그인 + 커넥션서버 조회. 실패 시 예외(메시지에 원인).</summary>
    public async Task<string> TestAsync(Corporation corp, CancellationToken ct)
    {
        await LoginAsync(corp, ct).ConfigureAwait(false);
        using var doc = await GetMonitorAsync(corp, "connection-servers", ct, "v3", "v2", "v1").ConfigureAwait(false);
        var css = ParseConnectionServers(doc.RootElement);
        var ok = css.Count(c => Healthy.Cs(c.Status));
        var ver = css.Select(c => c.Version).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        var conn = css.Sum(c => c.ConnectionCount ?? 0);
        return $"로그인 성공 · 커넥션서버 {ok}/{css.Count} 정상" +
               (ver != null ? $" · 버전 {ver}" : "") + $" · 연결 {conn}";
    }

    /// <summary>세션 목록(상세 탭 온디맨드). 사용자/머신/풀 이름을 가능한 만큼 해석한다.</summary>
    public async Task<List<SessionRow>> FetchSessionsAsync(Corporation corp, int maxRows, CancellationToken ct)
    {
        var (sessions, _) = await GetInventoryListAsync(corp, "sessions", maxRows, ct).ConfigureAwait(false);

        // 이름 해석용 보조 맵(실패해도 세션 목록은 반환).
        var machineNames = new Dictionary<string, string>();
        var rdsNames = new Dictionary<string, string>();
        var poolNames = new Dictionary<string, string>();
        var farmNames = new Dictionary<string, string>();
        try
        {
            var (machines, _) = await GetInventoryListAsync(corp, "machines", 10000, ct).ConfigureAwait(false);
            foreach (var m in machines)
                if (J.Str(m, "id") is string id && J.Str(m, "name") is string nm) machineNames[id] = nm;
        }
        catch { /* ignore */ }
        try
        {
            var (pools, _) = await GetInventoryListAsync(corp, "desktop-pools", 500, ct, "v2", "v1").ConfigureAwait(false);
            foreach (var p in pools)
                if (J.Str(p, "id") is string id && J.Str(p, "name") is string nm) poolNames[id] = nm;
        }
        catch { /* ignore */ }
        try
        {
            using var doc = await GetMonitorAsync(corp, "farms", ct, "v1").ConfigureAwait(false);
            foreach (var f in ParseFarms(doc.RootElement))
                if (f.Id != null) farmNames[f.Id] = f.Name;
        }
        catch { /* ignore */ }
        try
        {
            using var doc = await GetMonitorAsync(corp, "rds-servers", ct, "v1").ConfigureAwait(false);
            foreach (var el in doc.RootElement.EnumerateArray())
                if (J.Str(el, "id") is string id && J.Str(el, "name") is string nm) rdsNames[id] = nm;
        }
        catch { /* ignore */ }

        // 사용자명 해석(캐시 + 상한 병렬 조회).
        var userIds = sessions.Select(s => J.Str(s, "user_id")).Where(x => x != null).Cast<string>()
            .Distinct().Where(id => !_userNameCache.ContainsKey(id)).Take(300).ToList();
        using (var sem = new SemaphoreSlim(8))
        {
            var lookups = userIds.Select(async id =>
            {
                await sem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var r = await GetRawAsync(corp, "/rest/external/v1/ad-users-or-groups/" + Uri.EscapeDataString(id), ct).ConfigureAwait(false);
                    if (r.Code != HttpStatusCode.OK) return;
                    using var doc = JsonDocument.Parse(r.Body);
                    var name = J.Str(doc.RootElement, "login_name", "display_name", "name");
                    var domain = J.Str(doc.RootElement, "domain", "netbios_name");
                    if (name != null) _userNameCache[id] = domain != null ? $"{domain}\\{name}" : name;
                }
                catch { /* 개별 실패 무시 */ }
                finally { sem.Release(); }
            }).ToList();
            try { await Task.WhenAll(lookups).ConfigureAwait(false); } catch { /* ignore */ }
        }

        var rows = new List<SessionRow>();
        foreach (var s in sessions)
        {
            var userId = J.Str(s, "user_id");
            var machineId = J.Str(s, "machine_id");
            var rdsId = J.Str(s, "rds_server_id");
            var poolId = J.Str(s, "desktop_pool_id");
            var farmId = J.Str(s, "farm_id");
            var client = J.Child(s, "client_data");
            var start = J.Long(s, "start_time");
            rows.Add(new SessionRow
            {
                User = userId != null && _userNameCache.TryGetValue(userId, out var un) ? un : (userId ?? ""),
                Machine = machineId != null && machineNames.TryGetValue(machineId, out var mn) ? mn
                    : rdsId != null && rdsNames.TryGetValue(rdsId, out var rn) ? rn
                    : machineId ?? rdsId ?? "",
                PoolOrFarm = poolId != null && poolNames.TryGetValue(poolId, out var pn) ? pn
                    : farmId != null && farmNames.TryGetValue(farmId, out var fn) ? fn
                    : poolId ?? farmId ?? "",
                Type = J.Str(s, "session_type") ?? "",
                State = J.Str(s, "session_state") ?? "",
                StartUtc = start is long ms && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null,
                Protocol = J.Str(s, "session_protocol", "protocol"),
                ClientAddress = client is JsonElement c1 ? J.Str(c1, "address", "ip_address") : null,
                ClientType = client is JsonElement c2 ? J.Str(c2, "type", "name") : null,
                ClientVersion = client is JsonElement c3 ? J.Str(c3, "version") : null,
                AgentVersion = J.Str(s, "agent_version"),
            });
        }
        return rows;
    }

    /// <summary>머신 목록(상세 탭 온디맨드).</summary>
    public async Task<List<MachineRow>> FetchMachinesAsync(Corporation corp, int maxRows, CancellationToken ct)
    {
        var poolNames = new Dictionary<string, string>();
        try
        {
            var (pools, _) = await GetInventoryListAsync(corp, "desktop-pools", 500, ct, "v2", "v1").ConfigureAwait(false);
            foreach (var p in pools)
                if (J.Str(p, "id") is string id && J.Str(p, "name") is string nm) poolNames[id] = nm;
        }
        catch { /* ignore */ }

        var (machines, _) = await GetInventoryListAsync(corp, "machines", maxRows, ct).ConfigureAwait(false);
        return machines.Select(m =>
        {
            var poolId = J.Str(m, "desktop_pool_id");
            return new MachineRow
            {
                Name = J.Str(m, "name") ?? "?",
                State = J.Str(m, "state") ?? "",
                Pool = poolId != null && poolNames.TryGetValue(poolId, out var pn) ? pn : poolId ?? "",
                AgentVersion = J.Str(m, "agent_version"),
                OperatingSystem = J.Str(m, "operating_system"),
            };
        }).ToList();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true; // GetHttp가 새 HttpClient를 되살리지 못하게(리셋 후 유령 요청 방지)
            _http?.Dispose();
            _http = null;
        }
        try { _authSem.Dispose(); } catch { /* in-flight 대기자가 있으면 무시 */ }
    }
}

/// <summary>JsonElement 방어적 접근 헬퍼 — 필드 후보 다중, 타입 불일치/누락 허용.</summary>
internal static class J
{
    public static JsonElement? Child(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
           v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

    public static string? Str(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return v.ToString();
        }
        return null;
    }

    public static int? Int(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (int)d;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        }
        return null;
    }

    public static long? Long(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i)) return i;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        }
        return null;
    }

    public static bool? Bool(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }
}
