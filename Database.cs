using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HorizonServiceMonitor;

/// <summary>이력 차트/표용 경량 시계열 포인트(숫자 요약 — 상세 JSON 없이).</summary>
public sealed class HistoryPoint
{
    public long CorpId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public HealthStatus Status { get; set; }
    public int Sessions { get; set; }
    public int CsOk { get; set; }
    public int CsTotal { get; set; }
    public int GwOk { get; set; }
    public int GwTotal { get; set; }
    public int RdsOk { get; set; }
    public int RdsTotal { get; set; }
    public int ProblemMachines { get; set; }
    public double DurationMs { get; set; }
    public string? Error { get; set; }
    public bool HasDetail { get; set; }
}

/// <summary>
/// 자체 SQLite DB(자기완결형 파일). %LOCALAPPDATA%\HorizonServiceMonitor\monitor.db 에 보관.
/// 단일 연결을 열어두고 lock으로 직렬화(동시 점검이 write를 경쟁하지 않게). WAL로 write 가속.
/// 시계열(samples)은 숫자 요약만 매 주기 저장하고, 무거운 상세 JSON은 상태 전이/주기적 하트비트
/// 시점에만 저장해 장기 보존에도 DB가 비대해지지 않게 한다(상세는 별도 보존기간으로 정리).
/// </summary>
public sealed class Database : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public string DbPath { get; }

    public Database(string? overridePath = null)
    {
        DbPath = overridePath ?? DefaultDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=3000;");
        CreateSchema();
    }

    public static string DefaultDbPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HorizonServiceMonitor");
        return Path.Combine(dir, "monitor.db");
    }

    private void CreateSchema()
    {
        Exec(@"
            CREATE TABLE IF NOT EXISTS corps (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                code TEXT NOT NULL DEFAULT '',
                region TEXT NOT NULL DEFAULT '',
                server_url TEXT NOT NULL DEFAULT '',
                domain TEXT NOT NULL DEFAULT '',
                username TEXT NOT NULL DEFAULT '',
                password_enc TEXT NOT NULL DEFAULT '',
                interval_sec INTEGER NOT NULL DEFAULT 60,
                timeout_ms INTEGER NOT NULL DEFAULT 15000,
                enabled INTEGER NOT NULL DEFAULT 1,
                sort INTEGER NOT NULL DEFAULT 0,
                collect_inventory INTEGER NOT NULL DEFAULT 1,
                notes TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS samples (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                corp_id INTEGER NOT NULL,
                ts INTEGER NOT NULL,
                status INTEGER NOT NULL,
                sessions INTEGER NOT NULL DEFAULT 0,
                cs_ok INTEGER NOT NULL DEFAULT 0,
                cs_total INTEGER NOT NULL DEFAULT 0,
                gw_ok INTEGER NOT NULL DEFAULT 0,
                gw_total INTEGER NOT NULL DEFAULT 0,
                rds_ok INTEGER NOT NULL DEFAULT 0,
                rds_total INTEGER NOT NULL DEFAULT 0,
                farm_ok INTEGER NOT NULL DEFAULT 0,
                farm_total INTEGER NOT NULL DEFAULT 0,
                problem_machines INTEGER NOT NULL DEFAULT 0,
                cert_days INTEGER,
                duration_ms REAL NOT NULL DEFAULT 0,
                error TEXT,
                detail_json TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_samples_corp_ts ON samples (corp_id, ts);
            CREATE INDEX IF NOT EXISTS idx_samples_ts ON samples (ts);
            -- 상세 prune(UPDATE ... WHERE detail_json IS NOT NULL)이 상세가 남은 소량 행만 스캔하도록.
            CREATE INDEX IF NOT EXISTS idx_samples_detail_ts ON samples (ts) WHERE detail_json IS NOT NULL;
            CREATE TABLE IF NOT EXISTS latest_detail (
                corp_id INTEGER PRIMARY KEY,
                ts INTEGER NOT NULL,
                detail_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT);
        ");
    }

    private void Exec(string sql)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    // ── corps ────────────────────────────────────────────────────────────────
    public List<Corporation> ListCorps()
    {
        lock (_gate)
        {
            var list = new List<Corporation>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT id,name,code,region,server_url,domain,username,password_enc,
                interval_sec,timeout_ms,enabled,sort,collect_inventory,notes FROM corps ORDER BY sort, code, name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new Corporation
                {
                    Id = r.GetInt64(0),
                    Name = r.GetString(1),
                    Code = r.GetString(2),
                    Region = r.GetString(3),
                    ServerUrl = r.GetString(4),
                    Domain = r.GetString(5),
                    Username = r.GetString(6),
                    PasswordEnc = r.GetString(7),
                    IntervalSec = r.GetInt32(8),
                    TimeoutMs = r.GetInt32(9),
                    Enabled = r.GetInt32(10) != 0,
                    Sort = r.GetInt32(11),
                    CollectInventory = r.GetInt32(12) != 0,
                    Notes = r.GetString(13),
                });
            }
            return list;
        }
    }

    public long UpsertCorp(Corporation c)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            if (c.Id > 0)
            {
                cmd.CommandText = @"UPDATE corps SET name=$n,code=$c,region=$rg,server_url=$u,domain=$d,username=$us,
                    password_enc=$pw,interval_sec=$iv,timeout_ms=$to,enabled=$en,sort=$so,collect_inventory=$ci,notes=$no WHERE id=$id";
                cmd.Parameters.AddWithValue("$id", c.Id);
            }
            else
            {
                cmd.CommandText = @"INSERT INTO corps (name,code,region,server_url,domain,username,password_enc,
                    interval_sec,timeout_ms,enabled,sort,collect_inventory,notes)
                    VALUES ($n,$c,$rg,$u,$d,$us,$pw,$iv,$to,$en,$so,$ci,$no)";
            }
            cmd.Parameters.AddWithValue("$n", c.Name);
            cmd.Parameters.AddWithValue("$c", c.Code ?? "");
            cmd.Parameters.AddWithValue("$rg", c.Region ?? "");
            cmd.Parameters.AddWithValue("$u", c.ServerUrl ?? "");
            cmd.Parameters.AddWithValue("$d", c.Domain ?? "");
            cmd.Parameters.AddWithValue("$us", c.Username ?? "");
            cmd.Parameters.AddWithValue("$pw", c.PasswordEnc ?? "");
            cmd.Parameters.AddWithValue("$iv", c.IntervalSec);
            cmd.Parameters.AddWithValue("$to", c.TimeoutMs);
            cmd.Parameters.AddWithValue("$en", c.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$so", c.Sort);
            cmd.Parameters.AddWithValue("$ci", c.CollectInventory ? 1 : 0);
            cmd.Parameters.AddWithValue("$no", c.Notes ?? "");
            cmd.ExecuteNonQuery();
            if (c.Id > 0) return c.Id;
            using var idCmd = _conn.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid()";
            var id = (long)(idCmd.ExecuteScalar() ?? 0L);
            c.Id = id;
            return id;
        }
    }

    public void DeleteCorp(long id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM samples WHERE corp_id=$id; DELETE FROM latest_detail WHERE corp_id=$id; DELETE FROM corps WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public int CountCorps()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM corps";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }
    }

    // ── samples ──────────────────────────────────────────────────────────────
    /// <summary>점검 1회 결과 저장. withDetail=true면 상세 JSON도 시계열에 남긴다(전이/하트비트).</summary>
    public void InsertSample(CorpSnapshot s, bool withDetail)
    {
        var json = JsonSerializer.Serialize(s, JsonOpts);
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO samples (corp_id,ts,status,sessions,cs_ok,cs_total,gw_ok,gw_total,
                    rds_ok,rds_total,farm_ok,farm_total,problem_machines,cert_days,duration_ms,error,detail_json)
                    VALUES ($c,$t,$s,$se,$co,$ct,$go,$gt,$ro,$rt,$fo,$ft,$pm,$cd,$dm,$err,$dj)";
                cmd.Parameters.AddWithValue("$c", s.CorpId);
                cmd.Parameters.AddWithValue("$t", ToMs(s.TimestampUtc));
                cmd.Parameters.AddWithValue("$s", (int)s.Status);
                cmd.Parameters.AddWithValue("$se", s.SessionTotal);
                cmd.Parameters.AddWithValue("$co", s.CsOk);
                cmd.Parameters.AddWithValue("$ct", s.CsTotal);
                cmd.Parameters.AddWithValue("$go", s.GwOk);
                cmd.Parameters.AddWithValue("$gt", s.GwTotal);
                cmd.Parameters.AddWithValue("$ro", s.RdsOk);
                cmd.Parameters.AddWithValue("$rt", s.RdsTotal);
                cmd.Parameters.AddWithValue("$fo", s.FarmOk);
                cmd.Parameters.AddWithValue("$ft", s.FarmTotal);
                cmd.Parameters.AddWithValue("$pm", s.ProblemMachines.Count);
                cmd.Parameters.AddWithValue("$cd", (object?)s.ApiCertExpiryDays ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$dm", s.DurationMs);
                cmd.Parameters.AddWithValue("$err", (object?)s.Error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$dj", withDetail ? json : DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO latest_detail (corp_id,ts,detail_json) VALUES ($c,$t,$j)
                    ON CONFLICT(corp_id) DO UPDATE SET ts=$t, detail_json=$j";
                cmd.Parameters.AddWithValue("$c", s.CorpId);
                cmd.Parameters.AddWithValue("$t", ToMs(s.TimestampUtc));
                cmd.Parameters.AddWithValue("$j", json);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>법인별 마지막 상세 스냅샷(기동 시 직전 상태 복원용).</summary>
    public Dictionary<long, CorpSnapshot> LatestDetailAll()
    {
        lock (_gate)
        {
            var map = new Dictionary<long, CorpSnapshot>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT corp_id, detail_json FROM latest_detail";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                try
                {
                    var snap = JsonSerializer.Deserialize<CorpSnapshot>(r.GetString(1), JsonOpts);
                    if (snap != null) map[r.GetInt64(0)] = snap;
                }
                catch { /* 손상된 JSON은 무시(다음 점검에서 갱신) */ }
            }
            return map;
        }
    }

    /// <summary>이력 차트용 숫자 시계열(오래된→최신).</summary>
    public List<HistoryPoint> History(long corpId, DateTime sinceUtc, int maxRows = 50000)
    {
        lock (_gate)
        {
            var list = new List<HistoryPoint>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT ts,status,sessions,cs_ok,cs_total,gw_ok,gw_total,rds_ok,rds_total,
                problem_machines,duration_ms,error,(detail_json IS NOT NULL)
                FROM samples WHERE corp_id=$c AND ts>=$since ORDER BY ts DESC LIMIT $lim";
            cmd.Parameters.AddWithValue("$c", corpId);
            cmd.Parameters.AddWithValue("$since", ToMs(sinceUtc));
            cmd.Parameters.AddWithValue("$lim", maxRows);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new HistoryPoint
                {
                    CorpId = corpId,
                    TimestampUtc = FromMs(r.GetInt64(0)),
                    Status = (HealthStatus)r.GetInt32(1),
                    Sessions = r.GetInt32(2),
                    CsOk = r.GetInt32(3), CsTotal = r.GetInt32(4),
                    GwOk = r.GetInt32(5), GwTotal = r.GetInt32(6),
                    RdsOk = r.GetInt32(7), RdsTotal = r.GetInt32(8),
                    ProblemMachines = r.GetInt32(9),
                    DurationMs = r.GetDouble(10),
                    Error = r.IsDBNull(11) ? null : r.GetString(11),
                    HasDetail = r.GetInt32(12) != 0,
                });
            }
            list.Reverse();
            return list;
        }
    }

    /// <summary>법인별 최근 N개 포인트(오래된→최신) — 카드 스파크라인용(가볍게).</summary>
    public List<HistoryPoint> RecentPoints(long corpId, int limit = 40)
    {
        lock (_gate)
        {
            var list = new List<HistoryPoint>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT ts,status,sessions,cs_ok,cs_total,gw_ok,gw_total,rds_ok,rds_total,
                problem_machines,duration_ms,error,(detail_json IS NOT NULL)
                FROM samples WHERE corp_id=$c ORDER BY ts DESC LIMIT $lim";
            cmd.Parameters.AddWithValue("$c", corpId);
            cmd.Parameters.AddWithValue("$lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new HistoryPoint
                {
                    CorpId = corpId,
                    TimestampUtc = FromMs(r.GetInt64(0)),
                    Status = (HealthStatus)r.GetInt32(1),
                    Sessions = r.GetInt32(2),
                    CsOk = r.GetInt32(3), CsTotal = r.GetInt32(4),
                    GwOk = r.GetInt32(5), GwTotal = r.GetInt32(6),
                    RdsOk = r.GetInt32(7), RdsTotal = r.GetInt32(8),
                    ProblemMachines = r.GetInt32(9),
                    DurationMs = r.GetDouble(10),
                    Error = r.IsDBNull(11) ? null : r.GetString(11),
                    HasDetail = r.GetInt32(12) != 0,
                });
            }
            list.Reverse();
            return list;
        }
    }

    /// <summary>특정 시점의 상세 스냅샷(있을 때만 — 전이/하트비트 시점) 조회.</summary>
    public CorpSnapshot? DetailAt(long corpId, DateTime tsUtc)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT detail_json FROM samples WHERE corp_id=$c AND ts=$t AND detail_json IS NOT NULL LIMIT 1";
            cmd.Parameters.AddWithValue("$c", corpId);
            cmd.Parameters.AddWithValue("$t", ToMs(tsUtc));
            var json = cmd.ExecuteScalar() as string;
            if (json == null) return null;
            try { return JsonSerializer.Deserialize<CorpSnapshot>(json, JsonOpts); } catch { return null; }
        }
    }

    /// <summary>보존기간 정리 — 숫자 시계열과 상세 JSON을 서로 다른 보존기간으로 정리한다.
    /// 청크 단위로 나눠 사이사이 락(_gate)을 놓아 UI 스레드 조회가 굶지 않게 한다
    /// (보존기간을 줄인 직후 대량 삭제로 단일 락을 길게 잡는 것을 방지).</summary>
    public void Prune(int retentionDays, int detailRetentionDays)
    {
        const int chunk = 20000;
        if (retentionDays > 0)
        {
            var before = ToMs(DateTime.UtcNow.AddDays(-retentionDays));
            while (ExecChunk("DELETE FROM samples WHERE id IN (SELECT id FROM samples WHERE ts < $before LIMIT $lim)", before, chunk) >= chunk) { }
        }
        if (detailRetentionDays > 0)
        {
            var before = ToMs(DateTime.UtcNow.AddDays(-detailRetentionDays));
            // 부분 인덱스(idx_samples_detail_ts) 덕에 상세가 남은 소량 행만 스캔한다.
            while (ExecChunk("UPDATE samples SET detail_json=NULL WHERE id IN (SELECT id FROM samples WHERE ts < $before AND detail_json IS NOT NULL LIMIT $lim)", before, chunk) >= chunk) { }
        }
        // 삭제된 법인의 고아 latest_detail 정리(수집 경쟁으로 재삽입된 행 포함).
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM latest_detail WHERE corp_id NOT IN (SELECT id FROM corps)";
            cmd.ExecuteNonQuery();
        }
    }

    private int ExecChunk(string sql, long before, int limit)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$before", before);
            cmd.Parameters.AddWithValue("$lim", limit);
            return cmd.ExecuteNonQuery();
        }
    }

    // ── settings ─────────────────────────────────────────────────────────────
    public string? GetSetting(string key)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO settings (key,value) VALUES ($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    public int GetIntSetting(string key, int fallback)
        => int.TryParse(GetSetting(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public bool GetBoolSetting(string key, bool fallback)
    {
        var v = GetSetting(key);
        return v == null ? fallback : v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static long ToMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    public void Dispose()
    {
        // 종료 시점에 in-flight 명령(InsertSample 등)이 실행 중이면 완료를 기다린 뒤 연결 파기.
        lock (_gate)
        {
            try { _conn.Dispose(); } catch { /* ignore */ }
        }
    }
}
