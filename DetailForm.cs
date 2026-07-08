using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HorizonServiceMonitor;

/// <summary>
/// 법인 상세 창 — 커넥션서버(내부 서비스 포함)/게이트웨이/세션호스트/풀·팜/세션/머신/인프라
/// (이벤트DB·AD·vCenter·SAML)/이력을 탭으로 보여준다. 수집 주기에 맞춰 자동 갱신되고,
/// 세션·머신 목록은 부하를 고려해 버튼 클릭 시 온디맨드로 조회한다.
/// </summary>
public sealed class DetailForm : Form
{
    private readonly Database _db;
    private readonly Poller _poller;
    private readonly Corporation _corp;
    private readonly CancellationTokenSource _cts = new();

    private readonly Label _headStatus = new() { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
    private readonly Label _headInfo = new() { AutoSize = true, ForeColor = Color.FromArgb(108, 117, 125) };
    private readonly Label _headError = new() { Dock = DockStyle.Bottom, AutoSize = false, Height = 0, ForeColor = Color.Firebrick, Padding = new Padding(12, 0, 8, 4) };

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };

    // 개요
    private readonly Label _ovCounts = new() { AutoSize = true, Font = new Font("Segoe UI", 10f), Padding = new Padding(4) };
    private readonly ListView _ovIssues = MakeList(new[] { ("구성요소", 130), ("이름", 220), ("상태", 140), ("상세", 420) });

    // 커넥션서버
    private readonly ListView _csList = MakeList(new[] { ("이름", 160), ("상태", 90), ("버전", 110), ("빌드", 90), ("연결수", 70), ("터널", 60), ("인증서 만료", 120), ("복제", 170), ("비고", 170) });
    private readonly ListView _svcList = MakeList(new[] { ("서비스", 260), ("상태", 120) });

    // 게이트웨이
    private readonly ListView _gwList = MakeList(new[] { ("이름", 170), ("상태", 100), ("유형", 90), ("버전", 110), ("활성 연결", 80), ("Blast", 70), ("PCoIP", 70), ("내부", 60) });

    // 세션호스트
    private readonly CheckBox _rdsOnlyProblem = new() { Text = "문제만 보기", AutoSize = true };
    private readonly ListView _rdsList = MakeList(new[] { ("이름", 170), ("팜", 120), ("상태", 90), ("활성화", 60), ("세션", 80), ("부하지수", 70), ("부하설정", 90), ("에이전트", 100), ("OS", 190) });

    // 풀·팜
    private readonly ListView _farmList = MakeList(new[] { ("팜", 180), ("상태", 90), ("유형", 100), ("호스트 수", 80), ("앱 수", 70) });
    private readonly ListView _poolList = MakeList(new[] { ("풀", 140), ("표시명", 140), ("상태", 80), ("유형", 90), ("소스", 110), ("활성", 55), ("프로비저닝", 80), ("머신", 60), ("문제", 60) });

    // 세션
    private readonly Label _sessSummary = new() { AutoSize = true, Padding = new Padding(4), Font = new Font("Segoe UI", 9.5f) };
    private readonly Button _sessFetch = new() { Text = "세션 목록 조회(온디맨드)", AutoSize = true };
    private readonly Button _sessCsv = new() { Text = "CSV 내보내기", AutoSize = true, Enabled = false };
    private readonly Label _sessInfo = new() { AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(8, 6, 0, 0) };
    private readonly ListView _sessList = MakeList(new[] { ("사용자", 150), ("머신", 140), ("풀/팜", 120), ("유형", 90), ("상태", 100), ("시작(로컬)", 130), ("지속", 70), ("프로토콜", 70), ("클라이언트", 130), ("클라이언트 버전", 100), ("에이전트", 90) });
    private List<SessionRow> _sessRows = new();

    // 머신
    private readonly Button _machFetch = new() { Text = "전체 머신 조회(온디맨드)", AutoSize = true };
    private readonly ComboBox _machFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly Label _machInfo = new() { AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(8, 6, 0, 0) };
    private readonly ListView _machList = MakeList(new[] { ("이름", 190), ("상태", 170), ("풀", 150), ("에이전트", 110), ("OS", 240) });
    private List<MachineRow> _machRows = new();
    private bool _machFetched;

    // 인프라
    private readonly Label _evtLabel = new() { AutoSize = true, Padding = new Padding(4), Font = new Font("Segoe UI", 9.5f) };
    private readonly ListView _adList = MakeList(new[] { ("도메인(DNS)", 200), ("NetBIOS", 120), ("상태", 160), ("신뢰 관계", 160) });
    private readonly ListView _vcList = MakeList(new[] { ("vCenter", 260), ("상태", 110), ("버전", 110), ("데이터스토어(주의/전체)", 150) });
    private readonly ListView _samlList = MakeList(new[] { ("SAML 인증기", 260), ("상태", 120) });

    // 이력
    private readonly ComboBox _histRange = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly Label _histStats = new() { AutoSize = true, Padding = new Padding(10, 8, 0, 0) };
    private readonly HistoryChart _histChart = new() { Dock = DockStyle.Fill };
    private readonly ListView _histTrans = MakeList(new[] { ("시각(로컬)", 140), ("전이", 120), ("사유", 560) });
    private readonly Button _histCsv = new() { Text = "이력 CSV", AutoSize = true };

    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };
    private volatile bool _dirty = true;
    private DateTime _lastShownTs = DateTime.MinValue;

    public DetailForm(Database db, Poller poller, Corporation corp)
    {
        _db = db;
        _poller = poller;
        _corp = corp;

        Text = $"{corp.DisplayName} — Horizon 상세";
        Width = 1220;
        Height = 780;
        MinimumSize = new Size(980, 620);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        ShowIcon = false;
        ShowInTaskbar = false;

        // ── 헤더 ──────────────────────────────────────────────────────────────
        var head = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Color.FromArgb(248, 249, 250) };
        var headFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 8, 0), WrapContents = false };
        var title = new Label { Text = corp.DisplayName, AutoSize = true, Font = new Font("Segoe UI", 13f, FontStyle.Bold), Margin = new Padding(0, 2, 14, 0) };
        _headStatus.Margin = new Padding(0, 6, 14, 0);
        _headInfo.Margin = new Padding(0, 8, 0, 0);
        headFlow.Controls.Add(title);
        headFlow.Controls.Add(_headStatus);
        headFlow.Controls.Add(_headInfo);
        head.Controls.Add(headFlow);
        head.Controls.Add(_headError);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 4, 6, 0) };
        var refreshBtn = new Button { Text = "지금 수집", AutoSize = true };
        refreshBtn.Click += (_, _) => _poller.CheckOneNow(_corp.Id);
        toolbar.Controls.Add(refreshBtn);

        // ── 탭 구성 ───────────────────────────────────────────────────────────
        _tabs.TabPages.Add(MakeOverviewTab());
        _tabs.TabPages.Add(MakeCsTab());
        _tabs.TabPages.Add(MakeSimpleListTab("게이트웨이", _gwList));
        _tabs.TabPages.Add(MakeRdsTab());
        _tabs.TabPages.Add(MakePoolFarmTab());
        _tabs.TabPages.Add(MakeSessionsTab());
        _tabs.TabPages.Add(MakeMachinesTab());
        _tabs.TabPages.Add(MakeInfraTab());
        _tabs.TabPages.Add(MakeHistoryTab());

        Controls.Add(_tabs);
        Controls.Add(toolbar);
        Controls.Add(head);

        _poller.Updated += OnPollerUpdated;
        _uiTimer.Tick += (_, _) => { if (_dirty) { _dirty = false; RefreshData(); } };
        _uiTimer.Start();
        Load += (_, _) => { RefreshData(); RefreshHistory(); };
        FormClosed += (_, _) =>
        {
            _poller.Updated -= OnPollerUpdated;
            _uiTimer.Stop();
            _uiTimer.Dispose();
            try { _cts.Cancel(); _cts.Dispose(); } catch { /* ignore */ }
        };
    }

    private void OnPollerUpdated() => _dirty = true;

    private static ListView MakeList((string Header, int Width)[] cols)
    {
        var lv = new ListView
        {
            View = View.Details, FullRowSelect = true, HideSelection = false, Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
        };
        foreach (var (h, w) in cols) lv.Columns.Add(h, w);
        return lv;
    }

    private static TabPage MakeSimpleListTab(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }

    /// <summary>SplitContainer 생성 — SplitterDistance는 컨트롤 크기가 확정된 뒤 적용
    /// (기본 크기 150×100에서 생성자 지정 시 범위 검증 예외 방지).</summary>
    private static SplitContainer MakeSplit(Orientation o, int distance, FixedPanel fixedPanel = FixedPanel.None)
    {
        var sc = new SplitContainer { Dock = DockStyle.Fill, Orientation = o, FixedPanel = fixedPanel };
        void Apply(object? s, EventArgs e)
        {
            var dim = o == Orientation.Horizontal ? sc.Height : sc.Width;
            if (dim <= distance + sc.Panel2MinSize) return; // 아직 작음 — 다음 리사이즈에서
            try { sc.SplitterDistance = distance; } catch { /* ignore */ }
            sc.SizeChanged -= Apply;
        }
        sc.SizeChanged += Apply;
        return sc;
    }

    private TabPage MakeOverviewTab()
    {
        var page = new TabPage("개요");
        var split = MakeSplit(Orientation.Horizontal, 92, FixedPanel.Panel1);
        split.Panel1.Controls.Add(_ovCounts);
        var issuesLabel = new Label { Text = "이상 항목(정상이 아닌 구성요소)", Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Padding = new Padding(4, 6, 0, 0) };
        split.Panel2.Controls.Add(_ovIssues);
        split.Panel2.Controls.Add(issuesLabel);
        page.Controls.Add(split);
        return page;
    }

    private TabPage MakeCsTab()
    {
        var page = new TabPage("커넥션서버");
        var split = MakeSplit(Orientation.Horizontal, 220);
        split.Panel1.Controls.Add(_csList);
        var svcLabel = new Label { Text = "선택한 커넥션서버의 내부 서비스", Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Padding = new Padding(4, 6, 0, 0) };
        split.Panel2.Controls.Add(_svcList);
        split.Panel2.Controls.Add(svcLabel);
        _csList.SelectedIndexChanged += (_, _) => RefreshServices();
        page.Controls.Add(split);
        return page;
    }

    private TabPage MakeRdsTab()
    {
        var page = new TabPage("세션호스트");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(6, 6, 0, 0) };
        _rdsOnlyProblem.CheckedChanged += (_, _) => _dirty = true;
        top.Controls.Add(_rdsOnlyProblem);
        page.Controls.Add(_rdsList);
        page.Controls.Add(top);
        return page;
    }

    private TabPage MakePoolFarmTab()
    {
        var page = new TabPage("풀·팜");
        var split = MakeSplit(Orientation.Horizontal, 180);
        var farmLabel = new Label { Text = "팜(세션호스트 그룹)", Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Padding = new Padding(4, 4, 0, 0) };
        split.Panel1.Controls.Add(_farmList);
        split.Panel1.Controls.Add(farmLabel);
        var poolLabel = new Label { Text = "데스크톱 풀(인벤토리 수집 시 갱신)", Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Padding = new Padding(4, 4, 0, 0) };
        split.Panel2.Controls.Add(_poolList);
        split.Panel2.Controls.Add(poolLabel);
        page.Controls.Add(split);
        return page;
    }

    private TabPage MakeSessionsTab()
    {
        var page = new TabPage("세션");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 66, Padding = new Padding(4, 2, 0, 0) };
        top.SetFlowBreak(_sessSummary, true);
        _sessFetch.Click += async (_, _) => await FetchSessionsAsync();
        _sessCsv.Click += (_, _) => ExportSessionsCsv();
        top.Controls.Add(_sessSummary);
        top.Controls.Add(_sessFetch);
        top.Controls.Add(_sessCsv);
        top.Controls.Add(_sessInfo);
        page.Controls.Add(_sessList);
        page.Controls.Add(top);
        return page;
    }

    private TabPage MakeMachinesTab()
    {
        var page = new TabPage("머신");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(4, 4, 0, 0) };
        _machFetch.Click += async (_, _) => await FetchMachinesAsync();
        _machFilter.Items.AddRange(new object[] { "문제만", "전체" });
        _machFilter.SelectedIndex = 0;
        _machFilter.SelectedIndexChanged += (_, _) => RefreshMachines();
        top.Controls.Add(_machFilter);
        top.Controls.Add(_machFetch);
        top.Controls.Add(_machInfo);
        page.Controls.Add(_machList);
        page.Controls.Add(top);
        return page;
    }

    private TabPage MakeInfraTab()
    {
        var page = new TabPage("인프라");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(4) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        layout.Controls.Add(_evtLabel, 0, 0);
        layout.Controls.Add(WithTitle("AD 도메인", _adList), 0, 1);
        layout.Controls.Add(WithTitle("vCenter", _vcList), 0, 2);
        layout.Controls.Add(WithTitle("SAML 인증기", _samlList), 0, 3);
        page.Controls.Add(layout);
        return page;
    }

    private static Panel WithTitle(string title, Control c)
    {
        var p = new Panel { Dock = DockStyle.Fill };
        var l = new Label { Text = title, Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Padding = new Padding(2, 4, 0, 0) };
        p.Controls.Add(c);
        p.Controls.Add(l);
        return p;
    }

    private TabPage MakeHistoryTab()
    {
        var page = new TabPage("이력");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(6, 6, 0, 0) };
        _histRange.Items.AddRange(new object[] { "3시간", "12시간", "24시간", "7일", "30일", "90일" });
        _histRange.SelectedIndex = 2;
        _histRange.SelectedIndexChanged += (_, _) => RefreshHistory();
        _histCsv.Click += (_, _) => ExportHistoryCsv();
        top.Controls.Add(new Label { Text = "범위:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        top.Controls.Add(_histRange);
        top.Controls.Add(_histCsv);
        top.Controls.Add(_histStats);

        var split = MakeSplit(Orientation.Horizontal, 280);
        split.Panel1.Controls.Add(_histChart);
        var transLabel = new Label { Text = "상태 전이", Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Padding = new Padding(4, 4, 0, 0) };
        split.Panel2.Controls.Add(_histTrans);
        split.Panel2.Controls.Add(transLabel);
        page.Controls.Add(split);
        page.Controls.Add(top);
        return page;
    }

    // ── 데이터 갱신 ──────────────────────────────────────────────────────────
    private CorpSnapshot? Latest()
        => _poller.Snapshot().FirstOrDefault(x => x.Corp.Id == _corp.Id)?.Latest;

    private void RefreshData()
    {
        var s = Latest();
        var status = s?.Status ?? HealthStatus.Unknown;
        _headStatus.Text = MainForm.StatusText(status, true);
        _headStatus.ForeColor = MainForm.StatusColor(status, true);
        _headInfo.Text = s == null ? "수집 대기 중"
            : $"마지막 수집 {s.TimestampUtc.ToLocalTime():HH:mm:ss} · 소요 {s.DurationMs:F0}ms · 세션 {s.SessionTotal:N0}" +
              (s.ApiCertExpiryDays is int d ? $" · 접속 인증서 {d}일" : "");
        var err = s?.Error;
        _headError.Text = err ?? "";
        _headError.Height = string.IsNullOrEmpty(err) ? 0 : 20;

        if (s == null) return;
        // 같은 스냅샷이면 리스트 재구성 생략(선택 상태 보존).
        if (s.TimestampUtc == _lastShownTs) return;
        _lastShownTs = s.TimestampUtc;

        RefreshOverview(s);
        RefreshCsList(s);
        RefreshGw(s);
        RefreshRds(s);
        RefreshPoolsFarms(s);
        RefreshSessionSummary(s);
        RefreshInfra(s);
        if (!_machFetched) RefreshMachinesFromSnapshot(s);
        RefreshHistory();
    }

    private void RefreshOverview(CorpSnapshot s)
    {
        _ovCounts.Text =
            $"세션 {s.SessionTotal:N0} (연결 {s.Sessions?.Connected.ToString("N0") ?? "—"} / 끊김 {s.Sessions?.Disconnected.ToString("N0") ?? "—"})   ·   " +
            $"커넥션서버 {s.CsOk}/{s.CsTotal}   ·   게이트웨이 {s.GwOk}/{s.GwTotal}   ·   팜 {s.FarmOk}/{s.FarmTotal}   ·   " +
            $"세션호스트 {s.RdsOk}/{s.RdsTotal}\n" +
            $"데스크톱 풀 {s.DesktopPools.Count}개   ·   머신 {s.MachineTotal?.ToString("N0") ?? "—"}대   ·   문제 머신 {s.ProblemMachines.Count}대   ·   " +
            $"vCenter {s.VirtualCenters.Count} · AD {s.AdDomains.Count} · 수집 {s.DurationMs:F0}ms";

        _ovIssues.BeginUpdate();
        _ovIssues.Items.Clear();
        void Issue(string kind, string name, string status, string detail)
        {
            var it = new ListViewItem(new[] { kind, name, status, detail });
            it.ForeColor = Color.Firebrick;
            _ovIssues.Items.Add(it);
        }
        foreach (var c in s.ConnectionServers.Where(c => !Healthy.Cs(c.Status)))
            Issue("커넥션서버", c.Name, c.Status, c.Details ?? "");
        foreach (var c in s.ConnectionServers)
            foreach (var sv in c.Services.Where(v => !Healthy.CsService(v.Status)))
                Issue("CS 서비스", $"{c.Name} › {sv.Name}", sv.Status, "");
        foreach (var g in s.Gateways.Where(g => !Healthy.Gateway(g.Status)))
            Issue("게이트웨이", g.Name, g.Status, g.Type ?? "");
        foreach (var f in s.Farms.Where(f => !Healthy.Farm(f.Status)))
            Issue("팜", f.Name, f.Status, "");
        foreach (var p in s.DesktopPools.Where(Healthy.PoolProblem))
            Issue("풀", p.Name, p.Status ?? "", p.DisplayName ?? "");
        foreach (var r in s.RdsServers.Where(HealthEvaluator.RdsProblem))
            Issue("세션호스트", r.Name, r.Status, r.FarmName ?? "");
        if (s.EventDb != null && !Healthy.EventDb(s.EventDb.Status) &&
            !string.Equals(s.EventDb.Status, "NOT_CONFIGURED", StringComparison.OrdinalIgnoreCase))
            Issue("이벤트DB", s.EventDb.ServerName ?? "-", s.EventDb.Status, s.EventDb.Details ?? "");
        foreach (var a in s.AdDomains.Where(a => !Healthy.Ad(a.Status)))
            Issue("AD 도메인", a.DnsName, a.Status, a.TrustRelationship ?? "");
        foreach (var v in s.VirtualCenters.Where(v => !Healthy.Vc(v.Status)))
            Issue("vCenter", v.Name, v.Status, "");
        foreach (var v in s.SamlAuthenticators.Where(v => !Healthy.Saml(v.Status)))
            Issue("SAML", v.Label, v.Status, "");
        foreach (var m in s.ProblemMachines.Take(200))
            Issue("머신", m.Name, m.State, m.Pool ?? "");
        foreach (var e in s.EndpointErrors)
            Issue("수집", "-", "부분 실패", e);
        if (_ovIssues.Items.Count == 0)
        {
            var ok = new ListViewItem(new[] { "-", "-", "정상", "모든 구성요소 정상" }) { ForeColor = Color.SeaGreen };
            _ovIssues.Items.Add(ok);
        }
        _ovIssues.EndUpdate();
    }

    private void RefreshCsList(CorpSnapshot s)
    {
        var selected = _csList.SelectedItems.Count > 0 ? _csList.SelectedItems[0].Text : null;
        _csList.BeginUpdate();
        _csList.Items.Clear();
        foreach (var c in s.ConnectionServers)
        {
            var it = new ListViewItem(new[]
            {
                c.Name, c.Status, c.Version ?? "", c.Build ?? "",
                c.ConnectionCount?.ToString("N0") ?? "—",
                c.TunnelConnectionCount?.ToString("N0") ?? "—",
                c.CertExpiryUtc is DateTime exp ? exp.ToLocalTime().ToString("yyyy-MM-dd") + $" ({(int)Math.Floor((exp - DateTime.UtcNow).TotalDays)}일)" : "—",
                c.Replication ?? "—",
                c.Details ?? "",
            }) { Tag = c };
            if (!Healthy.Cs(c.Status)) it.ForeColor = Color.Firebrick;
            _csList.Items.Add(it);
        }
        _csList.EndUpdate();
        // 선택 복원(없으면 첫 항목).
        if (_csList.Items.Count > 0)
        {
            var restore = _csList.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Text == selected) ?? _csList.Items[0];
            restore.Selected = true;
        }
        RefreshServices();
    }

    private void RefreshServices()
    {
        _svcList.BeginUpdate();
        _svcList.Items.Clear();
        if (_csList.SelectedItems.Count > 0 && _csList.SelectedItems[0].Tag is ConnectionServerInfo c)
        {
            foreach (var sv in c.Services)
            {
                var it = new ListViewItem(new[] { sv.Name, sv.Status });
                if (!Healthy.CsService(sv.Status)) it.ForeColor = Color.Firebrick;
                _svcList.Items.Add(it);
            }
            if (c.Services.Count == 0)
                _svcList.Items.Add(new ListViewItem(new[] { "(서비스 정보 없음 — API 버전에 따라 미제공)", "" }) { ForeColor = Color.Gray });
        }
        _svcList.EndUpdate();
    }

    private void RefreshGw(CorpSnapshot s)
    {
        _gwList.BeginUpdate();
        _gwList.Items.Clear();
        foreach (var g in s.Gateways)
        {
            var it = new ListViewItem(new[]
            {
                g.Name, g.Status, g.Type ?? "", g.Version ?? "",
                g.ActiveConnections?.ToString("N0") ?? "—",
                g.BlastCount?.ToString("N0") ?? "—",
                g.PcoipCount?.ToString("N0") ?? "—",
                g.Internal == null ? "—" : g.Internal == true ? "예" : "아니오",
            });
            if (!Healthy.Gateway(g.Status)) it.ForeColor = Color.Firebrick;
            _gwList.Items.Add(it);
        }
        if (s.Gateways.Count == 0)
            _gwList.Items.Add(new ListViewItem(new[] { "(등록된 게이트웨이 없음)", "", "", "", "", "", "", "" }) { ForeColor = Color.Gray });
        _gwList.EndUpdate();
    }

    private void RefreshRds(CorpSnapshot s)
    {
        _rdsList.BeginUpdate();
        _rdsList.Items.Clear();
        var rows = _rdsOnlyProblem.Checked ? s.RdsServers.Where(HealthEvaluator.RdsProblem) : s.RdsServers;
        foreach (var r in rows)
        {
            var sess = r.SessionCount?.ToString("N0") ?? "—";
            if (r.MaxSessions is int mx && mx > 0) sess += $"/{mx:N0}";
            var it = new ListViewItem(new[]
            {
                r.Name, r.FarmName ?? r.FarmId ?? "", r.Status,
                r.Enabled == null ? "—" : r.Enabled == true ? "예" : "아니오",
                sess,
                r.LoadIndex?.ToString(CultureInfo.InvariantCulture) ?? "—",
                r.LoadPreference ?? "—",
                r.AgentVersion ?? "—",
                r.OperatingSystem ?? "—",
            });
            if (HealthEvaluator.RdsProblem(r)) it.ForeColor = Color.Firebrick;
            else if (r.Enabled == false) it.ForeColor = Color.Gray;
            else if (r.LoadIndex is int li && li >= _poller.Thresholds.RdsLoadWarn) it.ForeColor = Color.FromArgb(190, 120, 20);
            _rdsList.Items.Add(it);
        }
        if (_rdsList.Items.Count == 0)
            _rdsList.Items.Add(new ListViewItem(new[] { _rdsOnlyProblem.Checked ? "(문제 세션호스트 없음)" : "(세션호스트 없음 — VDI 전용 법인일 수 있음)", "", "", "", "", "", "", "", "" }) { ForeColor = Color.Gray });
        _rdsList.EndUpdate();
    }

    private void RefreshPoolsFarms(CorpSnapshot s)
    {
        _farmList.BeginUpdate();
        _farmList.Items.Clear();
        foreach (var f in s.Farms)
        {
            var it = new ListViewItem(new[]
            {
                f.Name, f.Status, f.Type ?? "",
                f.RdsServerCount?.ToString(CultureInfo.InvariantCulture) ?? "—",
                f.ApplicationCount?.ToString(CultureInfo.InvariantCulture) ?? "—",
            });
            if (!Healthy.Farm(f.Status)) it.ForeColor = Color.Firebrick;
            _farmList.Items.Add(it);
        }
        if (s.Farms.Count == 0)
            _farmList.Items.Add(new ListViewItem(new[] { "(팜 없음)", "", "", "", "" }) { ForeColor = Color.Gray });
        _farmList.EndUpdate();

        _poolList.BeginUpdate();
        _poolList.Items.Clear();
        foreach (var p in s.DesktopPools)
        {
            var it = new ListViewItem(new[]
            {
                p.Name, p.DisplayName ?? "", p.Status ?? "—", p.Type ?? "", p.Source ?? "",
                p.Enabled == null ? "—" : p.Enabled == true ? "예" : "아니오",
                p.ProvisioningEnabled == null ? "—" : p.ProvisioningEnabled == true ? "예" : "아니오",
                p.MachineCount?.ToString(CultureInfo.InvariantCulture) ?? "—",
                p.ProblemMachineCount?.ToString(CultureInfo.InvariantCulture) ?? "0",
            });
            if (p.Enabled == false) it.ForeColor = Color.Gray;
            if (Healthy.PoolProblem(p) || (p.ProblemMachineCount ?? 0) > 0) it.ForeColor = Color.Firebrick;
            _poolList.Items.Add(it);
        }
        if (s.DesktopPools.Count == 0)
            _poolList.Items.Add(new ListViewItem(new[] { "(풀 정보 없음 — 인벤토리 수집 대기 또는 비활성)", "", "", "", "", "", "", "", "" }) { ForeColor = Color.Gray });
        _poolList.EndUpdate();
    }

    private void RefreshSessionSummary(CorpSnapshot s)
    {
        var sum = s.Sessions;
        _sessSummary.Text = sum == null
            ? $"세션(주기 수집): 총 {s.SessionTotal:N0} — 상세 집계는 인벤토리 수집 활성 시 표시"
            : $"세션(주기 수집): 총 {sum.Total:N0} · 연결 {sum.Connected:N0}" + (sum.Idle > 0 ? $"(유휴 {sum.Idle:N0})" : "") +
              $" · 끊김 {sum.Disconnected:N0} · 대기 {sum.Pending:N0}" +
              $" · 데스크톱 {sum.Desktop:N0} · 앱 {sum.Application:N0}" +
              (sum.Users is int u ? $" · 사용자 {u:N0}명" : "") +
              (sum.Truncated ? " (상한 도달 — 일부만 집계)" : "");
    }

    private void RefreshInfra(CorpSnapshot s)
    {
        _evtLabel.Text = s.EventDb == null
            ? "이벤트 DB: 정보 없음"
            : $"이벤트 DB: {s.EventDb.Status}" +
              (s.EventDb.ServerName != null ? $" · 서버 {s.EventDb.ServerName}" : "") +
              (s.EventDb.EventCount is long n ? $" · 이벤트 {n:N0}건" : "") +
              (s.EventDb.Details != null ? $" · {s.EventDb.Details}" : "");
        _evtLabel.ForeColor = s.EventDb != null && !Healthy.EventDb(s.EventDb.Status) &&
            !string.Equals(s.EventDb.Status, "NOT_CONFIGURED", StringComparison.OrdinalIgnoreCase)
            ? Color.Firebrick : Color.FromArgb(33, 37, 41);

        _adList.BeginUpdate();
        _adList.Items.Clear();
        foreach (var a in s.AdDomains)
        {
            var it = new ListViewItem(new[] { a.DnsName, a.NetbiosName ?? "", a.Status, a.TrustRelationship ?? "" });
            if (!Healthy.Ad(a.Status)) it.ForeColor = Color.Firebrick;
            _adList.Items.Add(it);
        }
        _adList.EndUpdate();

        _vcList.BeginUpdate();
        _vcList.Items.Clear();
        foreach (var v in s.VirtualCenters)
        {
            var ds = v.DatastoreTotal is int t ? $"{v.DatastoreWarn ?? 0}/{t}" : "—";
            var it = new ListViewItem(new[] { v.Name, v.Status, v.Version ?? "", ds });
            if (!Healthy.Vc(v.Status) || (v.DatastoreWarn ?? 0) > 0) it.ForeColor = Color.Firebrick;
            _vcList.Items.Add(it);
        }
        _vcList.EndUpdate();

        _samlList.BeginUpdate();
        _samlList.Items.Clear();
        foreach (var v in s.SamlAuthenticators)
        {
            var it = new ListViewItem(new[] { v.Label, v.Status });
            if (!Healthy.Saml(v.Status)) it.ForeColor = Color.Firebrick;
            _samlList.Items.Add(it);
        }
        if (s.SamlAuthenticators.Count == 0)
            _samlList.Items.Add(new ListViewItem(new[] { "(SAML 인증기 없음)", "" }) { ForeColor = Color.Gray });
        _samlList.EndUpdate();
    }

    // ── 세션(온디맨드) ───────────────────────────────────────────────────────
    private async Task FetchSessionsAsync()
    {
        _sessFetch.Enabled = false;
        _sessInfo.Text = "조회 중… (규모에 따라 수십 초 걸릴 수 있음)";
        try
        {
            var client = _poller.GetClient(_corp.Id);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(120));
            var rows = await client.FetchSessionsAsync(_corp, 20000, cts.Token);
            if (IsDisposed) return;
            _sessRows = rows;
            _sessList.BeginUpdate();
            _sessList.Items.Clear();
            foreach (var r in rows)
            {
                string dur = "—";
                if (r.StartUtc is DateTime st)
                {
                    var mins = (DateTime.UtcNow - st).TotalMinutes;
                    dur = mins < 60 ? $"{mins:F0}분" : mins < 1440 ? $"{mins / 60:F1}시간" : $"{mins / 1440:F1}일";
                }
                _sessList.Items.Add(new ListViewItem(new[]
                {
                    r.User, r.Machine, r.PoolOrFarm, r.Type, r.State,
                    r.StartUtc?.ToLocalTime().ToString("MM-dd HH:mm") ?? "—", dur,
                    r.Protocol ?? "—", FormatClient(r), r.ClientVersion ?? "—", r.AgentVersion ?? "—",
                }));
            }
            _sessList.EndUpdate();
            _sessInfo.Text = $"{rows.Count:N0}건 조회됨 ({DateTime.Now:HH:mm:ss})";
            _sessCsv.Enabled = rows.Count > 0;
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _sessInfo.Text = "조회 실패: " + (ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            if (!IsDisposed) _sessFetch.Enabled = true;
        }
    }

    private static string FormatClient(SessionRow r)
    {
        var parts = new[] { r.ClientAddress, r.ClientType }.Where(x => !string.IsNullOrEmpty(x)).ToArray();
        return parts.Length == 0 ? "—" : string.Join(" · ", parts);
    }

    private void ExportSessionsCsv()
    {
        using var sfd = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"horizon-sessions-{_corp.Code}-{DateTime.Now:yyyyMMdd-HHmm}.csv" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var w = new System.IO.StreamWriter(sfd.FileName, false, new System.Text.UTF8Encoding(true));
            w.WriteLine("user,machine,pool_or_farm,type,state,start_utc,protocol,client_address,client_type,client_version,agent_version");
            foreach (var r in _sessRows)
            {
                string q(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
                w.WriteLine(string.Join(",", q(r.User), q(r.Machine), q(r.PoolOrFarm), q(r.Type), q(r.State),
                    q(r.StartUtc?.ToString("u", CultureInfo.InvariantCulture)), q(r.Protocol), q(r.ClientAddress), q(r.ClientType), q(r.ClientVersion), q(r.AgentVersion)));
            }
            MessageBox.Show(this, "내보내기 완료", "CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "CSV 오류", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // ── 머신(온디맨드 + 스냅샷 문제 목록) ────────────────────────────────────
    private void RefreshMachinesFromSnapshot(CorpSnapshot s)
    {
        _machRows = s.ProblemMachines.Select(m => new MachineRow { Name = m.Name, State = m.State, Pool = m.Pool ?? "" }).ToList();
        _machInfo.Text = s.MachineTotal is int t
            ? $"주기 수집 기준: 전체 {t:N0}대 중 문제 {s.ProblemMachines.Count}대 (전체 목록은 '전체 머신 조회')"
            : "인벤토리 수집 대기 중 — '전체 머신 조회'로 즉시 확인 가능";
        RefreshMachines();
    }

    private async Task FetchMachinesAsync()
    {
        _machFetch.Enabled = false;
        _machInfo.Text = "조회 중…";
        try
        {
            var client = _poller.GetClient(_corp.Id);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(120));
            _machRows = await client.FetchMachinesAsync(_corp, 20000, cts.Token);
            if (IsDisposed) return;
            _machFetched = true;
            _machFilter.SelectedIndex = 1; // 전체 보기로 전환
            _machInfo.Text = $"{_machRows.Count:N0}대 조회됨 ({DateTime.Now:HH:mm:ss})";
            RefreshMachines();
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _machInfo.Text = "조회 실패: " + (ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            if (!IsDisposed) _machFetch.Enabled = true;
        }
    }

    private void RefreshMachines()
    {
        var onlyProblem = _machFilter.SelectedIndex == 0;
        _machList.BeginUpdate();
        _machList.Items.Clear();
        foreach (var m in _machRows.Where(m => !onlyProblem || Healthy.MachineProblemState(m.State)))
        {
            var it = new ListViewItem(new[] { m.Name, m.State, m.Pool, m.AgentVersion ?? "—", m.OperatingSystem ?? "—" });
            if (Healthy.MachineProblemState(m.State)) it.ForeColor = Color.Firebrick;
            _machList.Items.Add(it);
        }
        if (_machList.Items.Count == 0)
            _machList.Items.Add(new ListViewItem(new[] { onlyProblem ? "(문제 머신 없음)" : "(머신 없음)", "", "", "", "" }) { ForeColor = Color.Gray });
        _machList.EndUpdate();
    }

    // ── 이력 ─────────────────────────────────────────────────────────────────
    private TimeSpan HistRange() => _histRange.SelectedIndex switch
    {
        0 => TimeSpan.FromHours(3),
        1 => TimeSpan.FromHours(12),
        2 => TimeSpan.FromHours(24),
        3 => TimeSpan.FromDays(7),
        4 => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(90),
    };

    private void RefreshHistory()
    {
        var pts = _db.History(_corp.Id, DateTime.UtcNow - HistRange());
        _histChart.SetData(pts);

        if (pts.Count == 0) { _histStats.Text = "데이터 없음"; _histTrans.Items.Clear(); return; }
        var upCount = pts.Count(p => p.Status == HealthStatus.Up);
        var warnCount = pts.Count(p => p.Status == HealthStatus.Warn);
        var downCount = pts.Count(p => p.Status == HealthStatus.Down);
        _histStats.Text = $"표본 {pts.Count:N0} · 정상율 {(double)upCount / pts.Count * 100:F1}% · 주의 {warnCount} · 위험 {downCount}" +
                          $" · 세션 평균 {pts.Average(p => p.Sessions):F0} / 최대 {pts.Max(p => p.Sessions):N0}";

        _histTrans.BeginUpdate();
        _histTrans.Items.Clear();
        HealthStatus? prev = null;
        var transitions = new List<ListViewItem>();
        foreach (var p in pts)
        {
            if (prev != null && p.Status != prev)
            {
                var it = new ListViewItem(new[]
                {
                    p.TimestampUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"),
                    $"{StatusKo(prev.Value)} → {StatusKo(p.Status)}",
                    p.Error ?? "",
                }) { ForeColor = MainForm.StatusColor(p.Status, true) };
                transitions.Add(it);
            }
            prev = p.Status;
        }
        // 최신이 위로.
        transitions.Reverse();
        foreach (var it in transitions.Take(500)) _histTrans.Items.Add(it);
        if (_histTrans.Items.Count == 0)
            _histTrans.Items.Add(new ListViewItem(new[] { "-", "전이 없음", "선택한 범위 동안 상태 변화가 없었습니다." }) { ForeColor = Color.Gray });
        _histTrans.EndUpdate();
    }

    private static string StatusKo(HealthStatus s) => s switch
    {
        HealthStatus.Up => "정상", HealthStatus.Warn => "주의", HealthStatus.Down => "위험", _ => "대기",
    };

    private void ExportHistoryCsv()
    {
        using var sfd = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"horizon-history-{_corp.Code}-{DateTime.Now:yyyyMMdd-HHmm}.csv" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var pts = _db.History(_corp.Id, DateTime.UtcNow - HistRange());
            using var w = new System.IO.StreamWriter(sfd.FileName, false, new System.Text.UTF8Encoding(true));
            w.WriteLine("ts_utc,status,sessions,cs_ok,cs_total,gw_ok,gw_total,rds_ok,rds_total,problem_machines,duration_ms,error");
            foreach (var p in pts)
            {
                string q(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
                w.WriteLine(string.Join(",",
                    q(p.TimestampUtc.ToString("u", CultureInfo.InvariantCulture)), (int)p.Status, p.Sessions,
                    p.CsOk, p.CsTotal, p.GwOk, p.GwTotal, p.RdsOk, p.RdsTotal, p.ProblemMachines,
                    p.DurationMs.ToString("F0", CultureInfo.InvariantCulture), q(p.Error)));
            }
            MessageBox.Show(this, "내보내기 완료", "CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "CSV 오류", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    /// <summary>이력 차트 — 세션 수 라인 + 상태 색 점 + 위험 구간 세로선. 축 라벨 포함.</summary>
    private sealed class HistoryChart : Panel
    {
        private static readonly Font FAxis = new("Segoe UI", 8f);
        private List<HistoryPoint> _pts = new();

        public HistoryChart() { DoubleBuffered = true; BackColor = Color.White; }

        public void SetData(List<HistoryPoint> pts) { _pts = pts; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var area = new Rectangle(52, 12, Math.Max(60, Width - 66), Math.Max(40, Height - 44));
            using var gray = new SolidBrush(Color.FromArgb(134, 142, 150));

            if (_pts.Count == 0)
            {
                g.DrawString("데이터 없음", FAxis, gray, area.X, area.Y + area.Height / 2 - 6);
                return;
            }

            double max = Math.Max(1, _pts.Max(p => p.Sessions)) * 1.15;
            var t0 = _pts[0].TimestampUtc;
            var t1 = _pts[^1].TimestampUtc;
            double span = Math.Max(1, (t1 - t0).TotalSeconds);
            float X(DateTime t) => area.X + (float)(area.Width * (t - t0).TotalSeconds / span);
            float Y(double v) => area.Bottom - (float)(area.Height * Math.Min(v, max) / max);

            // 격자 + Y축 라벨
            using (var grid = new Pen(Color.FromArgb(238, 240, 243)))
            {
                for (int i = 0; i <= 4; i++)
                {
                    float y = area.Y + area.Height * i / 4f;
                    g.DrawLine(grid, area.X, y, area.Right, y);
                    var v = max * (4 - i) / 4;
                    g.DrawString(v.ToString("N0"), FAxis, gray, 4, y - 6);
                }
            }
            // X축 라벨(시작/중간/끝)
            g.DrawString(t0.ToLocalTime().ToString("MM-dd HH:mm"), FAxis, gray, area.X, area.Bottom + 4);
            var mid = t0.AddSeconds(span / 2);
            g.DrawString(mid.ToLocalTime().ToString("MM-dd HH:mm"), FAxis, gray, area.X + area.Width / 2 - 30, area.Bottom + 4);
            var endText = t1.ToLocalTime().ToString("MM-dd HH:mm");
            g.DrawString(endText, FAxis, gray, area.Right - g.MeasureString(endText, FAxis).Width, area.Bottom + 4);

            // 위험(다운) 세로선 먼저(라인 아래 깔림)
            foreach (var p in _pts.Where(p => p.Status == HealthStatus.Down))
            {
                using var pen = new Pen(Color.FromArgb(70, 214, 60, 60));
                var x = X(p.TimestampUtc);
                g.DrawLine(pen, x, area.Y, x, area.Bottom);
            }

            // 세션 라인
            var line = _pts.Select(p => new PointF(X(p.TimestampUtc), Y(p.Sessions))).ToArray();
            if (line.Length > 1)
            {
                using var pen = new Pen(Color.FromArgb(160, 52, 199, 89), 1.6f);
                g.DrawLines(pen, line);
            }

            // 상태 점
            foreach (var p in _pts)
            {
                var c = MainForm.StatusColor(p.Status, true);
                using var br = new SolidBrush(c);
                float r = p.Status == HealthStatus.Up ? 1.6f : 2.6f;
                g.FillEllipse(br, X(p.TimestampUtc) - r, Y(p.Sessions) - r, r * 2, r * 2);
            }
        }
    }
}
