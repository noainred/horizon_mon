using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace HorizonServiceMonitor;

/// <summary>
/// 메인 창 — 요약 헤더 + 리전별 법인 상태 카드(스파크라인) / 상세 표(전환).
/// 더블클릭으로 법인 상세(커넥션서버/서비스/게이트웨이/세션호스트/세션/이력)를 연다.
/// 닫기는 트레이로 숨김(App Context가 처리).
/// </summary>
public sealed class MainForm : Form
{
    private readonly Database _db;
    private readonly Poller _poller;

    private readonly SummaryHeader _header = new();
    private readonly FlowLayoutPanel _dashboard = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoScroll = true, BackColor = Color.FromArgb(243, 244, 246), Padding = new Padding(12) };
    private readonly List<Label> _groupHeaders = new();
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly Label _summary = new();
    private readonly ToolStripButton _viewCards;
    private readonly ToolStripButton _viewTable;
    private readonly ToolTip _cardTips = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new();

    private enum ViewMode { Cards, Table }
    private readonly Dictionary<long, CorpCard> _cards = new();
    private string _layoutSig = "";
    private ViewMode _view = ViewMode.Cards;
    private int _sortCol = -1;      // 표 정렬 컬럼(-1=기본)
    private bool _sortAsc = true;   // 정렬 방향
    private volatile bool _dirty = true;
    private int _uiTick;
    private Icon? _formIcon;

    public MainForm(Database db, Poller poller)
    {
        _db = db;
        _poller = poller;

        Text = "Horizon Service Monitor";
        Width = 1180;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 520);
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.White;

        var tool = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2), Renderer = new ToolStripProfessionalRenderer() };
        tool.Items.Add(new ToolStripButton("지금 전체 수집", null, (_, _) => _poller.CheckAllNow()));
        tool.Items.Add(new ToolStripButton("설정(법인 관리)", null, (_, _) => OpenSettings()));
        tool.Items.Add(new ToolStripButton("CSV 내보내기", null, (_, _) => ExportCsv()));
        tool.Items.Add(new ToolStripButton("새로고침", null, (_, _) => { _dirty = true; }));
        tool.Items.Add(new ToolStripSeparator());
        _viewCards = new ToolStripButton("카드", null, (_, _) => SetView(ViewMode.Cards)) { Checked = true };
        _viewTable = new ToolStripButton("표", null, (_, _) => SetView(ViewMode.Table));
        tool.Items.Add(_viewCards);
        tool.Items.Add(_viewTable);
        tool.Items.Add(new ToolStripButton("DB 위치 열기", null, (_, _) => OpenDbFolder()) { Alignment = ToolStripItemAlignment.Right });

        _header.Dock = DockStyle.Top;
        _header.Height = 74;

        _summary.Dock = DockStyle.Bottom;
        _summary.Height = 24;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.Padding = new Padding(10, 0, 0, 0);
        _summary.ForeColor = Color.FromArgb(108, 117, 125);
        _summary.BackColor = Color.FromArgb(248, 249, 250);

        _dashboard.Resize += (_, _) => UpdateHeaderWidths();

        SetupGrid();
        _content.Controls.Add(_dashboard);
        _content.Controls.Add(_grid);

        Controls.Add(_content);
        Controls.Add(_summary);
        Controls.Add(_header);
        Controls.Add(tool);

        _uiTimer.Interval = 1000;
        _uiTimer.Tick += (_, _) => { if (_dirty || (++_uiTick % 5 == 0)) { _dirty = false; RefreshAll(); } };
        _uiTimer.Start();

        _poller.Updated += OnPollerUpdated;
        Load += (_, _) => RefreshAll();
    }

    private void OnPollerUpdated() => _dirty = true;

    private void SetView(ViewMode v)
    {
        _view = v;
        _viewCards.Checked = v == ViewMode.Cards;
        _viewTable.Checked = v == ViewMode.Table;
        _dashboard.Visible = v == ViewMode.Cards;
        _grid.Visible = v == ViewMode.Table;
        _dirty = true;
    }

    // ── 공통 새로고침 ─────────────────────────────────────────────────────────
    private void RefreshAll()
    {
        var snap = _poller.Snapshot();
        UpdateHeader(snap);
        switch (_view)
        {
            case ViewMode.Cards: RefreshDashboard(snap); break;
            case ViewMode.Table: RefreshGrid(snap); break;
        }
        _summary.Text = $"DB: {_db.DbPath}  ·  v{AppVersion.Current}";
    }

    private void UpdateHeader(List<CorpStatus> snap)
    {
        var en = snap.Where(x => x.Corp.Enabled).ToList();
        _header.SetCounts(
            en.Count(x => x.Status == HealthStatus.Up),
            en.Count(x => x.Status == HealthStatus.Warn),
            en.Count(x => x.Status == HealthStatus.Down),
            en.Count(x => x.Status == HealthStatus.Unknown),
            snap.Count,
            en.Sum(x => x.Latest?.SessionTotal ?? 0),
            en.Sum(x => x.Latest?.RdsTotal ?? 0));
    }

    // ── 카드 대시보드 ─────────────────────────────────────────────────────────
    private void RefreshDashboard(List<CorpStatus> snap)
    {
        var sig = string.Join("|", snap.Select(x => x.Corp.Id + ":" + x.Corp.Region));
        if (sig != _layoutSig) { RebuildDashboard(snap); _layoutSig = sig; }
        foreach (var cs in snap)
        {
            if (!_cards.TryGetValue(cs.Corp.Id, out var card)) continue;
            card.SetData(cs, _db.RecentPoints(cs.Corp.Id, 40));
            _cardTips.SetToolTip(card, cs.Latest?.Error ?? "");
        }
    }

    private void RebuildDashboard(List<CorpStatus> snap)
    {
        _dashboard.SuspendLayout();
        // 반복 중 컬렉션 변경 방지 — 사본으로 순회하며 파기(자식 카드도 함께 파기됨).
        var old = _dashboard.Controls.Cast<Control>().ToList();
        _dashboard.Controls.Clear();
        foreach (var c in old) c.Dispose();
        _cards.Clear();
        _groupHeaders.Clear();

        if (snap.Count == 0)
        {
            _dashboard.Controls.Add(new Label
            {
                Text = "등록된 법인이 없습니다. 상단 '설정(법인 관리)'에서 커넥션서버 주소/계정을 추가하세요.",
                AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(8, 20, 8, 8),
            });
            _dashboard.ResumeLayout();
            return;
        }

        // 리전별로 그룹(등장 순서 보존). 각 그룹 헤더는 한 줄 전체를 차지(FlowBreak).
        var groups = new List<string>();
        var byRegion = new Dictionary<string, List<CorpStatus>>();
        foreach (var cs in snap)
        {
            var rg = string.IsNullOrWhiteSpace(cs.Corp.Region) ? "미지정" : cs.Corp.Region;
            if (!byRegion.ContainsKey(rg)) { byRegion[rg] = new List<CorpStatus>(); groups.Add(rg); }
            byRegion[rg].Add(cs);
        }

        foreach (var rg in groups)
        {
            var header = new Label
            {
                Text = rg, AutoSize = false, Height = 30, Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(52, 58, 64), TextAlign = ContentAlignment.BottomLeft,
                Margin = new Padding(4, 8, 4, 2),
            };
            _dashboard.Controls.Add(header);
            _dashboard.SetFlowBreak(header, true); // 헤더 다음 카드는 새 줄부터
            _groupHeaders.Add(header);

            CorpCard? last = null;
            foreach (var cs in byRegion[rg])
            {
                var card = new CorpCard { Tag = cs.Corp.Id };
                card.DoubleClick += (s, _) => OpenDetailFor((long)((Control)s!).Tag!);
                _cards[cs.Corp.Id] = card;
                _dashboard.Controls.Add(card);
                last = card;
            }
            if (last != null) _dashboard.SetFlowBreak(last, true); // 그룹 끝 → 다음 헤더는 새 줄
        }
        _dashboard.ResumeLayout();
        UpdateHeaderWidths();
    }

    // 그룹 헤더가 항상 한 줄 전체를 차지하도록 폭을 대시보드 클라이언트 폭에 맞춘다.
    private void UpdateHeaderWidths()
    {
        int w = _dashboard.ClientSize.Width - _dashboard.Padding.Horizontal - 8;
        if (w < 100) w = 100;
        foreach (var h in _groupHeaders) h.Width = w - h.Margin.Horizontal;
    }

    // ── 상세 표 ───────────────────────────────────────────────────────────────
    private void SetupGrid()
    {
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(238, 240, 243);
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _grid.RowTemplate.Height = 30;
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].Tag is long id) OpenDetailFor(id); };

        void Col(string name, string header, int fill)
            => _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = fill });
        Col("status", "상태", 7);
        Col("corp", "법인", 14);
        Col("region", "리전", 10);
        Col("server", "서버", 18);
        Col("sessions", "세션", 7);
        Col("cs", "CS", 7);
        Col("gw", "GW", 7);
        Col("farm", "팜", 7);
        Col("rds", "세션호스트", 9);
        Col("problem", "문제머신", 8);
        Col("cert", "인증서(일)", 8);
        Col("checked", "마지막 수집", 11);
        // 제목 클릭 정렬(오름/내림 토글). 자동 새로고침에도 유지되도록 데이터 정렬 방식 사용.
        foreach (DataGridViewColumn c in _grid.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
        _grid.ColumnHeaderMouseClick += (_, e) => OnHeaderClick(e.ColumnIndex);
    }

    private void OnHeaderClick(int col)
    {
        if (col < 0) return;
        if (_sortCol == col) _sortAsc = !_sortAsc; else { _sortCol = col; _sortAsc = true; }
        foreach (DataGridViewColumn c in _grid.Columns) c.HeaderCell.SortGlyphDirection = SortOrder.None;
        _grid.Columns[col].HeaderCell.SortGlyphDirection = _sortAsc ? SortOrder.Ascending : SortOrder.Descending;
        _dirty = true;
    }

    private static int? MinCertDays(CorpSnapshot? s)
    {
        if (s == null) return null;
        int? min = s.ApiCertExpiryDays;
        foreach (var c in s.ConnectionServers)
        {
            if (c.CertExpiryUtc is DateTime exp)
            {
                var d = (int)Math.Floor((exp - DateTime.UtcNow).TotalDays);
                if (min == null || d < min) min = d;
            }
        }
        return min;
    }

    private List<CorpStatus> SortSnap(List<CorpStatus> snap)
    {
        if (_sortCol < 0) return snap;
        bool numeric = _sortCol is 0 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11;
        double sentinel = _sortAsc ? double.PositiveInfinity : double.NegativeInfinity;
        if (numeric)
        {
            double Key(CorpStatus cs)
            {
                var s = cs.Latest;
                return _sortCol switch
                {
                    0 => SeverityRank(cs.Status, cs.Corp.Enabled),
                    4 => s?.SessionTotal ?? sentinel,
                    5 => s != null && s.CsTotal > 0 ? (double)s.CsOk / s.CsTotal : sentinel,
                    6 => s != null && s.GwTotal > 0 ? (double)s.GwOk / s.GwTotal : sentinel,
                    7 => s != null && s.FarmTotal > 0 ? (double)s.FarmOk / s.FarmTotal : sentinel,
                    8 => s != null && s.RdsTotal > 0 ? (double)s.RdsOk / s.RdsTotal : sentinel,
                    9 => s?.ProblemMachines.Count ?? sentinel,
                    10 => MinCertDays(s) ?? sentinel,
                    11 => s != null ? s.TimestampUtc.Ticks : sentinel,
                    _ => 0,
                };
            }
            return (_sortAsc ? snap.OrderBy(Key) : snap.OrderByDescending(Key)).ToList();
        }
        string SKey(CorpStatus cs) => _sortCol switch
        {
            1 => cs.Corp.DisplayName, 2 => cs.Corp.Region, 3 => cs.Corp.BaseUrl, _ => cs.Corp.DisplayName,
        } ?? "";
        return (_sortAsc ? snap.OrderBy(SKey, StringComparer.OrdinalIgnoreCase) : snap.OrderByDescending(SKey, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private static int SeverityRank(HealthStatus s, bool enabled) => !enabled ? 0 : s switch
    {
        HealthStatus.Down => 4, HealthStatus.Warn => 3, HealthStatus.Up => 2, _ => 1,
    };

    private void RefreshGrid(List<CorpStatus> snap)
    {
        snap = SortSnap(snap); // 현재 정렬 컬럼/방향으로 정렬(자동 새로고침에도 유지)
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var cs in snap)
        {
            var c = cs.Corp; var s = cs.Latest;
            string ratio(int ok, int total) => total == 0 ? "—" : $"{ok}/{total}";
            var idx = _grid.Rows.Add(
                StatusText(cs.Status, c.Enabled), c.DisplayName, c.Region, c.BaseUrl,
                s?.SessionTotal.ToString("N0", CultureInfo.InvariantCulture) ?? "—",
                s != null ? ratio(s.CsOk, s.CsTotal) : "—",
                s != null ? ratio(s.GwOk, s.GwTotal) : "—",
                s != null ? ratio(s.FarmOk, s.FarmTotal) : "—",
                s != null ? ratio(s.RdsOk, s.RdsTotal) : "—",
                s?.ProblemMachines.Count.ToString(CultureInfo.InvariantCulture) ?? "—",
                MinCertDays(s)?.ToString(CultureInfo.InvariantCulture) ?? "—",
                s == null ? (c.Enabled ? "대기" : "비활성") : AgeText(s.TimestampUtc));
            var row = _grid.Rows[idx];
            row.Tag = c.Id;
            var color = StatusColor(cs.Status, c.Enabled);
            row.Cells[0].Style.BackColor = color;
            row.Cells[0].Style.ForeColor = Color.White;
            row.Cells[0].Style.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            row.Cells[0].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            if (!c.Enabled) row.DefaultCellStyle.ForeColor = Color.Gray;
            if (s?.Error != null) foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = s.Error;
        }
        _grid.ResumeLayout();
    }

    // ── 액션 ──────────────────────────────────────────────────────────────────
    private void OpenSettings()
    {
        using var f = new SettingsForm(_db, _poller);
        if (f.ShowDialog(this) == DialogResult.OK)
        {
            _poller.ApplySettings();
            _poller.CheckAllNow();
            _layoutSig = "";
            _dirty = true;
        }
    }

    private void OpenDetailFor(long id)
    {
        var cs = _poller.Snapshot().FirstOrDefault(x => x.Corp.Id == id);
        if (cs == null) return;
        using var f = new DetailForm(_db, _poller, cs.Corp);
        f.ShowDialog(this);
        _dirty = true;
    }

    private void ExportCsv()
    {
        using var sfd = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"horizon-status-{DateTime.Now:yyyyMMdd-HHmm}.csv" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var snap = _poller.Snapshot();
            using var w = new System.IO.StreamWriter(sfd.FileName, false, new System.Text.UTF8Encoding(true));
            w.WriteLine("corp,code,region,server,status,sessions,cs_ok,cs_total,gw_ok,gw_total,farm_ok,farm_total,rds_ok,rds_total,problem_machines,cert_days,last_checked_utc,error");
            foreach (var cs in snap)
            {
                var c = cs.Corp; var s = cs.Latest;
                string q(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
                w.WriteLine(string.Join(",",
                    q(c.Name), q(c.Code), q(c.Region), q(c.BaseUrl), q(StatusText(cs.Status, c.Enabled)),
                    s?.SessionTotal.ToString(CultureInfo.InvariantCulture) ?? "",
                    s?.CsOk.ToString(CultureInfo.InvariantCulture) ?? "", s?.CsTotal.ToString(CultureInfo.InvariantCulture) ?? "",
                    s?.GwOk.ToString(CultureInfo.InvariantCulture) ?? "", s?.GwTotal.ToString(CultureInfo.InvariantCulture) ?? "",
                    s?.FarmOk.ToString(CultureInfo.InvariantCulture) ?? "", s?.FarmTotal.ToString(CultureInfo.InvariantCulture) ?? "",
                    s?.RdsOk.ToString(CultureInfo.InvariantCulture) ?? "", s?.RdsTotal.ToString(CultureInfo.InvariantCulture) ?? "",
                    s?.ProblemMachines.Count.ToString(CultureInfo.InvariantCulture) ?? "",
                    MinCertDays(s)?.ToString(CultureInfo.InvariantCulture) ?? "",
                    q(s?.TimestampUtc.ToString("u", CultureInfo.InvariantCulture) ?? ""), q(s?.Error)));
            }
            MessageBox.Show(this, "내보내기 완료", "CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "CSV 오류", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void OpenDbFolder()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_db.DbPath)!;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    // ── 상태 색/텍스트(공용) ─────────────────────────────────────────────────
    public static string StatusText(HealthStatus s, bool enabled) => !enabled ? "비활성" : s switch
    {
        HealthStatus.Up => "정상",
        HealthStatus.Warn => "주의",
        HealthStatus.Down => "위험",
        _ => "대기",
    };

    public static Color StatusColor(HealthStatus s, bool enabled) => !enabled ? Color.Silver : s switch
    {
        HealthStatus.Up => Color.FromArgb(34, 160, 90),
        HealthStatus.Warn => Color.FromArgb(214, 158, 30),
        HealthStatus.Down => Color.FromArgb(214, 60, 60),
        _ => Color.FromArgb(150, 150, 150),
    };

    private static string AgeText(DateTime utc)
    {
        var s = (DateTime.UtcNow - utc).TotalSeconds;
        if (s < 60) return $"{s:F0}초 전";
        if (s < 3600) return $"{s / 60:F0}분 전";
        if (s < 86400) return $"{s / 3600:F0}시간 전";
        return $"{s / 86400:F0}일 전";
    }

    /// <summary>트레이 상태 아이콘을 작업표시줄/창 아이콘에도 반영(공유 DefaultIcon 파기 방지).</summary>
    public void SetFormIcon(Icon icon)
    {
        Icon clone;
        try { clone = (Icon)icon.Clone(); } catch { return; }
        Icon = clone;
        var old = _formIcon;
        _formIcon = clone;
        if (old != null) { try { old.Dispose(); } catch { /* ignore */ } }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poller.Updated -= OnPollerUpdated;
        _uiTimer.Stop();
        try { _formIcon?.Dispose(); } catch { /* ignore */ }
        base.OnFormClosed(e);
    }

    /// <summary>상단 요약 헤더 — 제목 + 세션/호스트 합계 + 상태 카운트 알약(pill).</summary>
    private sealed class SummaryHeader : Panel
    {
        private static readonly Font FTitle = new("Segoe UI", 14f, FontStyle.Bold);
        private static readonly Font FSub = new("Segoe UI", 8.5f);
        private static readonly Font FPill = new("Segoe UI", 9.5f, FontStyle.Bold);
        private int _up, _warn, _down, _unknown, _total, _sessions, _hosts;

        public SummaryHeader() { DoubleBuffered = true; BackColor = Color.FromArgb(33, 41, 54); }

        public void SetCounts(int up, int warn, int down, int unknown, int total, int sessions, int hosts)
        {
            _up = up; _warn = warn; _down = down; _unknown = unknown; _total = total;
            _sessions = sessions; _hosts = hosts;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using var white = new SolidBrush(Color.White);
            using var sub = new SolidBrush(Color.FromArgb(173, 181, 189));
            g.DrawString("Horizon Service Monitor", FTitle, white, 16, 12);
            g.DrawString($"전세계 법인 Horizon 서비스 상태  ·  법인 {_total}개  ·  세션 {_sessions:N0}  ·  세션호스트 {_hosts}대", FSub, sub, 18, 44);

            // 오른쪽에서 왼쪽으로 알약 배치.
            var pills = new (string Label, int Count, Color Color)[]
            {
                ("정상", _up, StatusColor(HealthStatus.Up, true)),
                ("주의", _warn, StatusColor(HealthStatus.Warn, true)),
                ("위험", _down, StatusColor(HealthStatus.Down, true)),
                ("대기", _unknown, StatusColor(HealthStatus.Unknown, true)),
            };
            float x = Width - 16;
            for (int i = pills.Length - 1; i >= 0; i--)
            {
                var p = pills[i];
                var text = $"{p.Label} {p.Count}";
                var sz = g.MeasureString(text, FPill);
                float pw = sz.Width + 26, ph = 30, py = (Height - ph) / 2f;
                x -= pw;
                var rect = new RectangleF(x, py, pw, ph);
                using (var br = new SolidBrush(p.Color)) FillRoundedRect(g, rect, 15, br);
                g.DrawString(text, FPill, white, x + 13, py + (ph - sz.Height) / 2f);
                x -= 8;
            }
        }

        private static void FillRoundedRect(Graphics g, RectangleF r, float radius, Brush brush)
        {
            using var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            g.FillPath(brush, path);
        }
    }
}

/// <summary>앱 버전(어셈블리 정보 기반) — 하단 표시/업데이트 확인용.</summary>
public static class AppVersion
{
    public static string Current
    {
        get
        {
            var v = typeof(AppVersion).Assembly.GetName().Version;
            return v == null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
