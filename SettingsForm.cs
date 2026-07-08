using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HorizonServiceMonitor;

/// <summary>설정 — 법인(커넥션서버/계정) 추가·수정·삭제, 임계값/보존기간, 알림, 시작 시 자동 실행.</summary>
public sealed class SettingsForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "HorizonServiceMonitor";

    private readonly Database _db;
    private readonly Poller _poller;

    private readonly ListView _list = new()
    {
        View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
        Dock = DockStyle.Fill,
    };
    private readonly List<Corporation> _corps = new();
    private readonly List<long> _deletedIds = new();
    private readonly Dictionary<Corporation, string> _pendingPw = new(); // 새로 입력된 비밀번호(저장 시 암호화)
    private readonly Dictionary<Corporation, string> _origIdentity = new(); // 접속 정보 변경 감지용
    private Corporation? _selected;
    private bool _loadingFields;

    // 법인 편집 필드
    private readonly TextBox _name = new();
    private readonly TextBox _code = new();
    private readonly TextBox _region = new();
    private readonly TextBox _url = new();
    private readonly TextBox _domain = new();
    private readonly TextBox _user = new();
    private readonly TextBox _pass = new() { UseSystemPasswordChar = true, PlaceholderText = "변경할 때만 입력" };
    private readonly NumericUpDown _interval = new() { Minimum = 15, Maximum = 3600, Value = 60 };
    private readonly NumericUpDown _timeout = new() { Minimum = 2000, Maximum = 120000, Increment = 1000, Value = 15000 };
    private readonly CheckBox _enabled = new() { Text = "활성(수집 대상)", AutoSize = true };
    private readonly CheckBox _inventory = new() { Text = "머신/세션 인벤토리 상세 수집", AutoSize = true };
    private readonly TextBox _notes = new();
    private readonly Button _testBtn = new() { Text = "연결 테스트", AutoSize = true };
    private readonly Label _testResult = new() { AutoSize = true, ForeColor = Color.Gray, MaximumSize = new Size(380, 0) };
    private CancellationTokenSource? _testCts;
    private int _testGen;          // 테스트 세대 — 늦게 끝난 이전 테스트가 새 테스트 결과를 덮지 않게
    private bool _testRunning;

    // 전역 설정
    private readonly NumericUpDown _certWarn = new() { Minimum = 1, Maximum = 365, Value = 30 };
    private readonly NumericUpDown _rdsLoad = new() { Minimum = 10, Maximum = 100, Value = 90 };
    private readonly NumericUpDown _retention = new() { Minimum = 7, Maximum = 3650, Value = 365 };
    private readonly NumericUpDown _detailRetention = new() { Minimum = 1, Maximum = 365, Value = 14 };
    private readonly NumericUpDown _invEvery = new() { Minimum = 1, Maximum = 60, Value = 5 };
    private readonly NumericUpDown _concurrency = new() { Minimum = 1, Maximum = 16, Value = 4 };
    private readonly CheckBox _notify = new() { Text = "상태 전이 시 트레이 알림", AutoSize = true };
    private readonly CheckBox _autostart = new() { Text = "Windows 시작 시 자동 실행(현재 사용자)", AutoSize = true };

    public SettingsForm(Database db, Poller poller)
    {
        _db = db;
        _poller = poller;

        Text = "설정 — 법인 관리";
        Width = 1040;
        Height = 720;
        MinimumSize = new Size(920, 620);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowIcon = false;
        ShowInTaskbar = false;

        // ── 좌측: 법인 목록 + 버튼 ─────────────────────────────────────────────
        _list.Columns.Add("코드", 60);
        _list.Columns.Add("이름", 130);
        _list.Columns.Add("서버", 190);
        _list.Columns.Add("활성", 44);
        _list.SelectedIndexChanged += (_, _) => OnSelectCorp();

        var listBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 4, 0, 0) };
        listBtns.Controls.Add(MakeBtn("추가", (_, _) => AddCorp()));
        listBtns.Controls.Add(MakeBtn("삭제", (_, _) => RemoveCorp()));
        listBtns.Controls.Add(MakeBtn("위로", (_, _) => MoveCorp(-1)));
        listBtns.Controls.Add(MakeBtn("아래로", (_, _) => MoveCorp(1)));

        var left = new Panel { Dock = DockStyle.Left, Width = 440, Padding = new Padding(10) };
        left.Controls.Add(_list);
        left.Controls.Add(listBtns);

        // ── 우측: 법인 편집 필드 ───────────────────────────────────────────────
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(10) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control c)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 6, 2) };
            c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            if (c is TextBox or NumericUpDown) c.Margin = new Padding(0, 5, 10, 2);
            fields.Controls.Add(l);
            fields.Controls.Add(c);
        }
        Row("법인명", _name);
        Row("코드", _code);
        Row("리전(그룹)", _region);
        Row("커넥션서버 URL", _url);
        Row("도메인", _domain);
        Row("사용자(모니터링 계정)", _user);
        Row("비밀번호", _pass);
        Row("수집 주기(초)", _interval);
        Row("타임아웃(ms)", _timeout);
        Row("", _enabled);
        Row("", _inventory);
        Row("메모", _notes);

        var testPanel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(160, 0, 0, 0) };
        _testBtn.Click += async (_, _) => await TestConnectionAsync();
        testPanel.Controls.Add(_testBtn);
        testPanel.Controls.Add(_testResult);

        var hint = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(12, 8, 8, 0),
            Text = "URL 예: https://cs.corp.example.com (커넥션서버 또는 LB). 계정은 Horizon 관리자 콘솔의\n" +
                   "읽기 전용 관리자 역할(REST API 접근 가능)을 권장합니다. 비밀번호는 DPAPI(사용자 단위)로 암호화 저장됩니다.",
        };

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        right.Controls.Add(hint);
        right.Controls.Add(testPanel);
        right.Controls.Add(fields);

        // ── 하단: 전역 설정 + 저장/취소 ────────────────────────────────────────
        var global = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 128, ColumnCount = 6, Padding = new Padding(10, 6, 10, 0) };
        for (int i = 0; i < 3; i++)
        {
            global.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            global.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        }
        void GRow(int col, int row, string label, Control c)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 4, 2) };
            c.Anchor = AnchorStyles.Left;
            c.Width = 80;
            global.Controls.Add(l, col * 2, row);
            global.Controls.Add(c, col * 2 + 1, row);
        }
        GRow(0, 0, "인증서 경고(일)", _certWarn);
        GRow(1, 0, "세션호스트 부하 경고(%)", _rdsLoad);
        GRow(2, 0, "이력 보존(일)", _retention);
        GRow(0, 1, "상세 스냅샷 보존(일)", _detailRetention);
        GRow(1, 1, "인벤토리 수집 주기(회)", _invEvery);
        GRow(2, 1, "동시 수집 개수(재시작 적용)", _concurrency);
        global.Controls.Add(_notify, 0, 2);
        global.SetColumnSpan(_notify, 3);
        global.Controls.Add(_autostart, 3, 2);
        global.SetColumnSpan(_autostart, 3);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var ok = MakeBtn("저장", (_, _) => Save());
        var cancel = MakeBtn("취소", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        bottom.Controls.Add(ok);
        bottom.Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(global);
        Controls.Add(bottom);

        LoadData();
        FormClosed += (_, _) => { try { _testCts?.Cancel(); _testCts?.Dispose(); } catch { /* ignore */ } };
    }

    private static Button MakeBtn(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        b.Click += onClick;
        return b;
    }

    private void LoadData()
    {
        _corps.Clear();
        _corps.AddRange(_db.ListCorps());
        foreach (var c in _corps) _origIdentity[c] = IdentityOf(c);
        RebuildList(selectIndex: _corps.Count > 0 ? 0 : -1);

        _certWarn.Value = Clamp(_db.GetIntSetting("certWarnDays", 30), _certWarn);
        _rdsLoad.Value = Clamp(_db.GetIntSetting("rdsLoadWarn", 90), _rdsLoad);
        _retention.Value = Clamp(_db.GetIntSetting("retentionDays", 365), _retention);
        _detailRetention.Value = Clamp(_db.GetIntSetting("detailRetentionDays", 14), _detailRetention);
        _invEvery.Value = Clamp(_db.GetIntSetting("inventoryEveryN", 5), _invEvery);
        _concurrency.Value = Clamp(_db.GetIntSetting("collectConcurrency", 4), _concurrency);
        _notify.Checked = _db.GetBoolSetting("notifyEnabled", true);
        _autostart.Checked = IsAutostartEnabled();
    }

    private static decimal Clamp(int v, NumericUpDown n) => Math.Min(n.Maximum, Math.Max(n.Minimum, v));

    private static string IdentityOf(Corporation c)
        => string.Join("", c.BaseUrl, c.Domain, c.Username, c.PasswordEnc);

    private void RebuildList(int selectIndex)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var c in _corps)
        {
            var it = new ListViewItem(new[] { c.Code, c.Name, c.BaseUrl, c.Enabled ? "예" : "" }) { Tag = c };
            _list.Items.Add(it);
        }
        _list.EndUpdate();
        if (selectIndex >= 0 && selectIndex < _list.Items.Count)
        {
            _list.Items[selectIndex].Selected = true;
            _list.Items[selectIndex].EnsureVisible();
        }
        else if (_list.Items.Count == 0)
        {
            _selected = null;
            ApplyFieldsFrom(null);
        }
    }

    private void OnSelectCorp()
    {
        CommitFields(); // 이전 선택의 편집 내용 반영
        _selected = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Corporation : null;
        ApplyFieldsFrom(_selected);
    }

    private void ApplyFieldsFrom(Corporation? c)
    {
        _loadingFields = true;
        try
        {
            _name.Text = c?.Name ?? "";
            _code.Text = c?.Code ?? "";
            _region.Text = c?.Region ?? "";
            _url.Text = c?.ServerUrl ?? "";
            _domain.Text = c?.Domain ?? "";
            _user.Text = c?.Username ?? "";
            _pass.Text = "";
            _pass.PlaceholderText = c != null && !string.IsNullOrEmpty(c.PasswordEnc) ? "저장됨 — 변경할 때만 입력" : "변경할 때만 입력";
            _interval.Value = Clamp(c?.IntervalSec ?? 60, _interval);
            _timeout.Value = Clamp(c?.TimeoutMs ?? 15000, _timeout);
            _enabled.Checked = c?.Enabled ?? false;
            _inventory.Checked = c?.CollectInventory ?? true;
            _notes.Text = c?.Notes ?? "";
            _testResult.Text = "";
            var editable = c != null;
            foreach (Control ctl in new Control[] { _name, _code, _region, _url, _domain, _user, _pass, _interval, _timeout, _enabled, _inventory, _notes })
                ctl.Enabled = editable;
            _testBtn.Enabled = editable && !_testRunning; // 테스트 진행 중 선택 변경으로 재활성화 금지
        }
        finally { _loadingFields = false; }
    }

    /// <summary>현재 편집 필드 값을 선택된 법인 객체에 반영(저장은 Save에서 일괄).</summary>
    private void CommitFields()
    {
        if (_loadingFields || _selected == null) return;
        var c = _selected;
        c.Name = _name.Text.Trim();
        c.Code = _code.Text.Trim();
        c.Region = _region.Text.Trim();
        c.ServerUrl = _url.Text.Trim();
        c.Domain = _domain.Text.Trim();
        c.Username = _user.Text.Trim();
        if (_pass.Text.Length > 0) _pendingPw[c] = _pass.Text;
        c.IntervalSec = (int)_interval.Value;
        c.TimeoutMs = (int)_timeout.Value;
        c.Enabled = _enabled.Checked;
        c.CollectInventory = _inventory.Checked;
        c.Notes = _notes.Text.Trim();
        // 목록 표시 갱신
        foreach (ListViewItem it in _list.Items)
        {
            if (!ReferenceEquals(it.Tag, c)) continue;
            it.SubItems[0].Text = c.Code;
            it.SubItems[1].Text = c.Name;
            it.SubItems[2].Text = c.BaseUrl;
            it.SubItems[3].Text = c.Enabled ? "예" : "";
            break;
        }
    }

    private void AddCorp()
    {
        CommitFields();
        var c = new Corporation { Name = "새 법인", Code = "", IntervalSec = 60, TimeoutMs = 15000, Enabled = false, CollectInventory = true };
        _corps.Add(c);
        _origIdentity[c] = IdentityOf(c);
        RebuildList(_corps.Count - 1);
    }

    private void RemoveCorp()
    {
        if (_selected == null) return;
        var c = _selected;
        if (MessageBox.Show(this, $"'{c.DisplayName}' 법인과 누적 이력을 삭제할까요?", "삭제 확인",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var idx = _corps.IndexOf(c);
        _corps.Remove(c);
        _pendingPw.Remove(c);
        if (c.Id > 0) _deletedIds.Add(c.Id);
        _selected = null;
        RebuildList(Math.Min(idx, _corps.Count - 1));
    }

    private void MoveCorp(int delta)
    {
        if (_selected == null) return;
        CommitFields();
        var idx = _corps.IndexOf(_selected);
        var ni = idx + delta;
        if (idx < 0 || ni < 0 || ni >= _corps.Count) return;
        (_corps[idx], _corps[ni]) = (_corps[ni], _corps[idx]);
        var keep = _selected;
        RebuildList(ni);
        // RebuildList가 선택 이벤트로 _selected를 갱신하지만, 확실히 유지.
        _selected = keep;
    }

    private async Task TestConnectionAsync()
    {
        CommitFields();
        if (_selected == null) return;
        var c = _selected;
        if (c.BaseUrl.Length == 0 || string.IsNullOrWhiteSpace(c.Username))
        {
            _testResult.ForeColor = Color.Firebrick;
            _testResult.Text = "URL/사용자를 먼저 입력하세요.";
            return;
        }
        // 테스트용 사본(새 비밀번호 입력 시 그것을 사용).
        var probe = new Corporation
        {
            Name = c.Name, Code = c.Code, ServerUrl = c.ServerUrl, Domain = c.Domain, Username = c.Username,
            PasswordEnc = _pendingPw.TryGetValue(c, out var pw) ? Crypto.Protect(pw) : c.PasswordEnc,
            TimeoutMs = c.TimeoutMs,
        };
        var gen = ++_testGen; // 이 테스트의 세대 — 이후 UI 갱신은 최신 세대만 반영
        _testRunning = true;
        _testBtn.Enabled = false;
        _testResult.ForeColor = Color.Gray;
        _testResult.Text = $"[{probe.DisplayName}] 테스트 중…";
        _testCts?.Cancel();
        _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            using var client = new HorizonClient();
            var msg = await client.TestAsync(probe, _testCts.Token);
            if (IsDisposed || gen != _testGen) return;
            _testResult.ForeColor = Color.SeaGreen;
            _testResult.Text = $"[{probe.DisplayName}] {msg}";
        }
        catch (Exception ex)
        {
            if (IsDisposed || gen != _testGen) return;
            _testResult.ForeColor = Color.Firebrick;
            _testResult.Text = $"[{probe.DisplayName}] 실패: " + (ex.InnerException?.Message ?? ex.Message);
        }
        finally
        {
            if (!IsDisposed && gen == _testGen)
            {
                _testRunning = false;
                _testBtn.Enabled = _selected != null;
            }
        }
    }

    private void Save()
    {
        CommitFields();
        foreach (var c in _corps)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
            {
                MessageBox.Show(this, "법인명이 비어 있는 항목이 있습니다.", "확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (c.Enabled && c.BaseUrl.Length == 0)
            {
                MessageBox.Show(this, $"'{c.DisplayName}' 법인이 활성인데 커넥션서버 URL이 없습니다.", "확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        // 비밀번호 암호화 반영 → 저장 → 접속 정보가 바뀐 법인은 세션 토큰 파기.
        foreach (var kv in _pendingPw) kv.Key.PasswordEnc = Crypto.Protect(kv.Value);
        for (int i = 0; i < _corps.Count; i++)
        {
            var c = _corps[i];
            c.Sort = i;
            _db.UpsertCorp(c);
            if (!_origIdentity.TryGetValue(c, out var orig) || orig != IdentityOf(c))
                _poller.ResetClient(c.Id);
        }
        foreach (var id in _deletedIds)
        {
            _db.DeleteCorp(id);
            _poller.ResetClient(id);
        }

        _db.SetSetting("certWarnDays", ((int)_certWarn.Value).ToString(CultureInfo.InvariantCulture));
        _db.SetSetting("rdsLoadWarn", ((int)_rdsLoad.Value).ToString(CultureInfo.InvariantCulture));
        _db.SetSetting("retentionDays", ((int)_retention.Value).ToString(CultureInfo.InvariantCulture));
        _db.SetSetting("detailRetentionDays", ((int)_detailRetention.Value).ToString(CultureInfo.InvariantCulture));
        _db.SetSetting("inventoryEveryN", ((int)_invEvery.Value).ToString(CultureInfo.InvariantCulture));
        _db.SetSetting("collectConcurrency", ((int)_concurrency.Value).ToString(CultureInfo.InvariantCulture));
        _db.SetSetting("notifyEnabled", _notify.Checked ? "1" : "0");
        SetAutostart(_autostart.Checked);

        DialogResult = DialogResult.OK;
        Close();
    }

    // ── 자동 실행(HKCU Run) ─────────────────────────────────────────────────
    private static bool IsAutostartEnabled()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey, false); return k?.GetValue(RunValue) != null; }
        catch { return false; }
    }

    private static void SetAutostart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (k == null) return;
            if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --hidden");
            else if (k.GetValue(RunValue) != null) k.DeleteValue(RunValue, false);
        }
        catch { /* 권한 없으면 무시 */ }
    }
}
