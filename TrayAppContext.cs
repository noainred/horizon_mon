using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HorizonServiceMonitor;

/// <summary>
/// 트레이 상주 애플리케이션 컨텍스트 — 메인 창을 닫으면(X) 트레이로 숨고, 트레이 메뉴의
/// '종료'를 눌러야만 실제로 프로그램이 끝난다. 트레이 아이콘 색상은 전체 상태(정상/주의/위험)를
/// 반영하고, 법인 상태 전이(정상↔주의↔위험) 시 풍선 알림을 띄운다.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly Database _db;
    private readonly Poller _poller;
    private readonly MainForm _main;
    private readonly NotifyIcon _tray;
    private readonly ConcurrentQueue<(string Title, string Text, ToolTipIcon Icon)> _notifications = new();
    private bool _exiting;
    private bool _balloonShown;
    private Icon? _currentIcon;
    private IntPtr _currentIconHandle = IntPtr.Zero;
    private HealthStatus _lastOverall = (HealthStatus)(-1);
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };
    private volatile bool _dirty;

    public TrayAppContext(string? dbPath = null, bool startHidden = false)
    {
        _db = new Database(dbPath);
        DefaultCorporations.SeedIfEmpty(_db);

        _poller = new Poller(_db);
        _poller.Start();

        _tray = new NotifyIcon
        {
            Text = "Horizon Service Monitor",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowMain();
        UpdateTrayIcon(HealthStatus.Unknown, force: true);

        _main = new MainForm(_db, _poller);
        _main.FormClosing += MainOnFormClosing;

        _poller.Updated += OnPollerUpdated;
        _poller.StatusChanged += OnStatusChanged;

        // 트레이 아이콘/툴팁/알림 갱신은 UI 스레드 타이머로 디바운스(수집 완료가 몰려도 초당 최대 1회).
        _uiTimer.Tick += (_, _) =>
        {
            if (_exiting) return;
            if (_dirty) { _dirty = false; RefreshTray(); }
            DrainNotifications();
        };
        _uiTimer.Start();

        if (!startHidden) _main.Show();
        else ShowStartupBalloon();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => ShowMain());
        menu.Items.Add("지금 전체 수집", null, (_, _) => _poller.CheckAllNow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitApp());
        return menu;
    }

    private void OnPollerUpdated()
    {
        // 백그라운드 스레드에서 호출 — 플래그만 세우고 실제 갱신은 UI 스레드 타이머가 수행(마샬링·디바운스).
        _dirty = true;
    }

    private void OnStatusChanged(Corporation corp, HealthStatus oldS, HealthStatus newS, string? reason)
    {
        // 백그라운드 스레드 — 큐에만 넣고 UI 타이머가 풍선을 띄운다.
        if (!_db.GetBoolSetting("notifyEnabled", true)) return;
        if (oldS == HealthStatus.Unknown && newS == HealthStatus.Up) return; // 최초 정상 확인은 조용히
        var icon = newS == HealthStatus.Down ? ToolTipIcon.Error
            : newS == HealthStatus.Warn ? ToolTipIcon.Warning : ToolTipIcon.Info;
        var text = $"{StatusKo(oldS)} → {StatusKo(newS)}";
        if (!string.IsNullOrWhiteSpace(reason)) text += "\n" + Truncate(reason, 180);
        _notifications.Enqueue(($"{corp.DisplayName} 상태 변경", text, icon));
    }

    private void DrainNotifications()
    {
        // 한 틱에 하나만(풍선 연타 방지). 나머지는 다음 틱.
        if (_notifications.TryDequeue(out var n))
        {
            try
            {
                _tray.BalloonTipTitle = Truncate(n.Title, 63);
                _tray.BalloonTipText = string.IsNullOrWhiteSpace(n.Text) ? "-" : Truncate(n.Text, 255);
                _tray.BalloonTipIcon = n.Icon;
                _tray.ShowBalloonTip(5000);
            }
            catch { /* ignore */ }
        }
    }

    private static string StatusKo(HealthStatus s) => s switch
    {
        HealthStatus.Up => "정상",
        HealthStatus.Warn => "주의",
        HealthStatus.Down => "위험",
        _ => "대기",
    };

    // NotifyIcon.Text는 63자 제한. 초과 시 안전하게 자른다.
    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n);

    private void RefreshTray()
    {
        if (_exiting) return;
        var snap = _poller.Snapshot(); // 1회 조회로 카운트와 전체 상태 모두 계산.
        int up = 0, warn = 0, down = 0, unknown = 0, sessions = 0;
        foreach (var cs in snap)
        {
            if (!cs.Corp.Enabled) continue;
            switch (cs.Status)
            {
                case HealthStatus.Up: up++; break;
                case HealthStatus.Warn: warn++; break;
                case HealthStatus.Down: down++; break;
                default: unknown++; break;
            }
            sessions += cs.Latest?.SessionTotal ?? 0;
        }
        var overall = (up + warn + down + unknown) == 0 ? HealthStatus.Unknown
            : down > 0 ? HealthStatus.Down
            : warn > 0 ? HealthStatus.Warn
            : (up == 0 && unknown > 0) ? HealthStatus.Unknown
            : HealthStatus.Up;
        _tray.Text = Truncate($"Horizon: 정상 {up}/주의 {warn}/위험 {down} · 세션 {sessions}", 63);
        UpdateTrayIcon(overall);
    }

    private void UpdateTrayIcon(HealthStatus status, bool force = false)
    {
        if (!force && status == _lastOverall) return;
        _lastOverall = status;

        var color = status switch
        {
            HealthStatus.Up => Color.FromArgb(34, 160, 90),
            HealthStatus.Warn => Color.FromArgb(214, 158, 30),
            HealthStatus.Down => Color.FromArgb(214, 60, 60),
            _ => Color.FromArgb(150, 150, 150),
        };

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var br = new SolidBrush(color);
            g.FillEllipse(br, 4, 4, 24, 24);
            using var pen = new Pen(Color.FromArgb(230, 255, 255, 255), 2);
            g.DrawEllipse(pen, 4, 4, 24, 24);
            // 'H' 글자로 Horizon 모니터임을 표시.
            using var f = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var tb = new SolidBrush(Color.White);
            var sz = g.MeasureString("H", f);
            g.DrawString("H", f, tb, 16 - sz.Width / 2, 16 - sz.Height / 2);
        }

        var newHandle = bmp.GetHicon();
        var newIcon = Icon.FromHandle(newHandle);
        var oldIcon = _currentIcon;
        var oldHandle = _currentIconHandle;
        _tray.Icon = newIcon;
        _currentIcon = newIcon;
        _currentIconHandle = newHandle;
        // 이전 아이콘/핸들 정리(핸들 누수 방지).
        try { oldIcon?.Dispose(); } catch { /* ignore */ }
        if (oldHandle != IntPtr.Zero) { try { DestroyIcon(oldHandle); } catch { /* ignore */ } }
        // 메인 창 아이콘도 맞춰준다(작업표시줄).
        try { if (_currentIcon != null) _main?.SetFormIcon(_currentIcon); } catch { /* ignore */ }
    }

    private void ShowMain()
    {
        if (_main.IsDisposed) return;
        _main.Show();
        if (_main.WindowState == FormWindowState.Minimized) _main.WindowState = FormWindowState.Normal;
        _main.Activate();
        _main.BringToFront();
    }

    private void MainOnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // 사용자가 X로 닫으면 종료가 아니라 트레이로 숨긴다. '종료' 메뉴만 실제 종료.
        if (_exiting) return;
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _main.Hide();
            ShowStartupBalloon();
        }
    }

    private void ShowStartupBalloon()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        try
        {
            _tray.BalloonTipTitle = "Horizon Service Monitor";
            _tray.BalloonTipText = "트레이에서 계속 실행 중입니다. 트레이 아이콘을 두 번 클릭하면 창이 열립니다. 완전히 끝내려면 트레이 메뉴 › 종료.";
            _tray.BalloonTipIcon = ToolTipIcon.Info;
            _tray.ShowBalloonTip(4000);
        }
        catch { /* ignore */ }
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _poller.Updated -= OnPollerUpdated;
        _poller.StatusChanged -= OnStatusChanged;
        try { _uiTimer.Stop(); _uiTimer.Dispose(); } catch { /* ignore */ }
        try { _poller.Stop(); } catch { /* ignore */ }
        try { _tray.Visible = false; } catch { /* ignore */ }
        try { _main.FormClosing -= MainOnFormClosing; _main.Close(); } catch { /* ignore */ }
        try { _tray.Dispose(); } catch { /* ignore */ }
        try { _currentIcon?.Dispose(); } catch { /* ignore */ }
        if (_currentIconHandle != IntPtr.Zero) { try { DestroyIcon(_currentIconHandle); } catch { /* ignore */ } }
        try { _poller.Dispose(); } catch { /* ignore */ }
        try { _db.Dispose(); } catch { /* ignore */ }
        ExitThread();
    }
}
