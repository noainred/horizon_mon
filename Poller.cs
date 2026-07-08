using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonServiceMonitor;

/// <summary>
/// 수집 엔진 — 법인별 주기(interval)에 맞춰 Horizon REST API로 상태를 수집하고 DB에 누적한다.
/// 동시성 제한(SemaphoreSlim), 법인별 재진입 방지(_inFlight), prune 스로틀을 적용해
/// 다수 법인·고지연(RTT 800ms+) 환경에서도 안정적으로 동작한다(폴러 중첩 실행 금지 원칙).
/// </summary>
public sealed class Poller : IDisposable
{
    private readonly Database _db;
    private readonly SemaphoreSlim _concurrency;
    private readonly object _gate = new();
    private readonly HashSet<long> _inFlight = new();
    private readonly Dictionary<long, DateTime> _lastCheck = new();
    private readonly ConcurrentDictionary<Task, byte> _running = new();   // 진행 중 수집 태스크(종료 시 드레인)
    private readonly ConcurrentDictionary<long, HorizonClient> _clients = new();
    private readonly ConcurrentDictionary<long, CorpSnapshot> _latest = new();
    private readonly ConcurrentDictionary<long, int> _pollCount = new();  // 법인별 수집 횟수(인벤토리/하트비트 주기용)
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _tick;
    private volatile bool _forceAll;

    public Thresholds Thresholds { get; private set; }
    public int RetentionDays { get; private set; } = 365;
    public int DetailRetentionDays { get; private set; } = 14;
    /// <summary>N번째 수집마다 1회 인벤토리(머신/세션/풀) 상세 수집 — 대규모 법인 부하 완화.</summary>
    public int InventoryEveryN { get; private set; } = 5;
    /// <summary>상세 JSON 시계열 하트비트 주기(수집 횟수 기준, 상태 전이 시에는 항상 저장).</summary>
    public int DetailHeartbeatEveryN { get; private set; } = 60;

    /// <summary>한 법인 수집이 끝날 때마다 발생(백그라운드 스레드). UI는 스스로 스레드 마샬링할 것.</summary>
    public event Action? Updated;
    /// <summary>법인 상태 전이(old → new) 시 발생 — 트레이 알림용(백그라운드 스레드).</summary>
    public event Action<Corporation, HealthStatus, HealthStatus, string?>? StatusChanged;

    public Poller(Database db)
    {
        _db = db;
        Thresholds = Thresholds.Load(db);
        RetentionDays = db.GetIntSetting("retentionDays", 365);
        DetailRetentionDays = db.GetIntSetting("detailRetentionDays", 14);
        InventoryEveryN = Math.Max(1, db.GetIntSetting("inventoryEveryN", 5));
        // 동시 수집 개수(기동 시 적용) — 13개 법인을 한꺼번에 수집하면 파싱이 몰리므로 평탄화.
        _concurrency = new SemaphoreSlim(Math.Clamp(db.GetIntSetting("collectConcurrency", 4), 1, 16));
        // 기동 시 직전 상태 복원(마지막 상세 스냅샷) — 재시작 직후에도 카드가 비지 않게.
        foreach (var kv in db.LatestDetailAll()) _latest[kv.Key] = kv.Value;
    }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _loop?.Wait(3000); } catch { /* ignore */ }
        // 진행 중이던 개별 수집 태스크를 배수 — 이후 _db.Dispose()가 in-flight writer와 겹치지 않게.
        try { Task.WaitAll(_running.Keys.ToArray(), 5000); } catch { /* 취소/타임아웃 무시 */ }
        _loop = null;
    }

    /// <summary>즉시 전체 재수집 요청(다음 틱에 모든 활성 법인 수집).</summary>
    public void CheckAllNow() => _forceAll = true;

    /// <summary>특정 법인만 즉시 재수집(다음 틱).</summary>
    public void CheckOneNow(long corpId)
    {
        lock (_gate) { _lastCheck.Remove(corpId); }
    }

    /// <summary>설정 변경 후 임계값/보존기간 재적용(동시 수집 개수는 재시작 시 적용).</summary>
    public void ApplySettings()
    {
        Thresholds = Thresholds.Load(_db);
        RetentionDays = _db.GetIntSetting("retentionDays", 365);
        DetailRetentionDays = _db.GetIntSetting("detailRetentionDays", 14);
        InventoryEveryN = Math.Max(1, _db.GetIntSetting("inventoryEveryN", 5));
    }

    /// <summary>법인 설정(주소/계정) 변경·삭제 시 세션 토큰 파기.</summary>
    public void ResetClient(long corpId)
    {
        if (_clients.TryRemove(corpId, out var c)) { try { c.Dispose(); } catch { /* ignore */ } }
        _latest.TryRemove(corpId, out _);
    }

    /// <summary>상세 뷰(세션/머신 목록 등)가 토큰을 재사용할 수 있게 클라이언트 공유.</summary>
    public HorizonClient GetClient(long corpId) => _clients.GetOrAdd(corpId, _ => new HorizonClient());

    /// <summary>현재 법인 + 최신 상태 스냅샷(UI 그리드용).</summary>
    public List<CorpStatus> Snapshot()
    {
        var corps = _db.ListCorps();
        return corps.Select(c => new CorpStatus
        {
            Corp = c,
            Latest = _latest.TryGetValue(c.Id, out var s) ? s : null,
        }).ToList();
    }

    public HealthStatus OverallStatus()
    {
        var snap = Snapshot().Where(s => s.Corp.Enabled).ToList();
        if (snap.Count == 0) return HealthStatus.Unknown;
        if (snap.Any(s => s.Status == HealthStatus.Down)) return HealthStatus.Down;
        if (snap.Any(s => s.Status == HealthStatus.Warn)) return HealthStatus.Warn;
        if (snap.All(s => s.Status == HealthStatus.Unknown)) return HealthStatus.Unknown;
        return HealthStatus.Up;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var force = _forceAll;
                _forceAll = false;
                var now = DateTime.UtcNow;
                foreach (var corp in _db.ListCorps())
                {
                    if (!corp.Enabled || corp.BaseUrl.Length == 0 || string.IsNullOrWhiteSpace(corp.Username)) continue;
                    lock (_gate)
                    {
                        if (_inFlight.Contains(corp.Id)) continue; // 진행 중이면 이번 틱 건너뜀(재진입 가드)
                        var due = force || !_lastCheck.TryGetValue(corp.Id, out var last)
                                  || (now - last).TotalSeconds >= Math.Max(15, corp.IntervalSec);
                        if (!due) continue;
                        _inFlight.Add(corp.Id);
                        _lastCheck[corp.Id] = now;
                    }
                    // 태스크를 추적해 Stop()에서 배수(fire-and-forget이지만 종료 시 대기 가능하게).
                    var t = RunCheckAsync(corp, token);
                    _running.TryAdd(t, 0);
                    _ = t.ContinueWith(x => _running.TryRemove(x, out _), TaskScheduler.Default);
                }

                // prune 스로틀: 약 10분마다(틱 1s 기준 600틱). ts 인덱스로 범위 삭제.
                if (++_tick % 600 == 0)
                {
                    try { _db.Prune(RetentionDays, DetailRetentionDays); } catch { /* ignore */ }
                }
            }
            catch { /* 루프는 죽지 않는다 */ }

            try { await Task.Delay(1000, token); } catch { break; }
        }
    }

    private async Task RunCheckAsync(Corporation corp, CancellationToken token)
    {
        bool acquired = false;
        try
        {
            await _concurrency.WaitAsync(token).ConfigureAwait(false);
            acquired = true;

            var count = _pollCount.AddOrUpdate(corp.Id, 1, (_, v) => v + 1);
            var includeInventory = corp.CollectInventory && (count == 1 || count % InventoryEveryN == 0);

            var client = _clients.GetOrAdd(corp.Id, _ => new HorizonClient());
            var snap = await client.FetchSnapshotAsync(corp, includeInventory, token).ConfigureAwait(false);

            // 인벤토리를 이번 주기에 건너뛰었으면 직전 값을 이월(카드 수치 깜빡임 방지 — 최대 N주기 지연).
            if (!includeInventory && _latest.TryGetValue(corp.Id, out var prev) && prev != null)
            {
                snap.Sessions ??= prev.Sessions;
                snap.MachineTotal ??= prev.MachineTotal;
                if (snap.ProblemMachines.Count == 0 && prev.ProblemMachines.Count > 0)
                    snap.ProblemMachines = prev.ProblemMachines;
                if (snap.DesktopPools.Count == 0 && prev.DesktopPools.Count > 0)
                    snap.DesktopPools = prev.DesktopPools;
            }

            HealthEvaluator.Evaluate(snap, Thresholds);

            var prevStatus = _latest.TryGetValue(corp.Id, out var p) ? p.Status : HealthStatus.Unknown;
            var transition = snap.Status != prevStatus;
            var withDetail = transition || count == 1 || count % DetailHeartbeatEveryN == 0;

            _db.InsertSample(snap, withDetail);
            _latest[corp.Id] = snap;

            if (transition)
            {
                try { StatusChanged?.Invoke(corp, prevStatus, snap.Status, snap.Error); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            // 수집 코드 자체의 예외(FetchSnapshot는 원칙적으로 던지지 않음) — Down 샘플로 환원.
            if (acquired && !token.IsCancellationRequested)
            {
                try
                {
                    var snap = new CorpSnapshot
                    {
                        CorpId = corp.Id,
                        TimestampUtc = DateTime.UtcNow,
                        Status = HealthStatus.Down,
                        Error = "수집 실패: " + (ex.InnerException?.Message ?? ex.Message),
                    };
                    var prevStatus = _latest.TryGetValue(corp.Id, out var p2) ? p2.Status : HealthStatus.Unknown;
                    _db.InsertSample(snap, prevStatus != HealthStatus.Down);
                    _latest[corp.Id] = snap;
                    if (prevStatus != snap.Status)
                    {
                        try { StatusChanged?.Invoke(corp, prevStatus, snap.Status, snap.Error); } catch { /* ignore */ }
                    }
                }
                catch { /* ignore */ }
            }
        }
        finally
        {
            // WaitAsync가 취소로 던져도(acquired=false) 재진입 가드는 반드시 해제(_inFlight 누수 방지).
            if (acquired) { try { _concurrency.Release(); } catch { /* ignore */ } }
            lock (_gate) { _inFlight.Remove(corp.Id); }
            if (acquired) { try { Updated?.Invoke(); } catch { /* ignore */ } }
        }
    }

    public void Dispose()
    {
        Stop();
        foreach (var c in _clients.Values) { try { c.Dispose(); } catch { /* ignore */ } }
        _clients.Clear();
        _cts?.Dispose();
        _concurrency.Dispose();
    }
}
