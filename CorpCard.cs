using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace HorizonServiceMonitor;

/// <summary>
/// 법인 1개를 시각적으로 보여주는 상태 카드 — 좌측 상태 색상 스트라이프, 큰 현재 세션 수,
/// CS/게이트웨이/팜/세션호스트/문제머신 지표, 하단 세션수 스파크라인.
/// 더블클릭 시 법인 상세 열기(부모가 처리).
/// </summary>
public sealed class CorpCard : Panel
{
    private static readonly Font FName = new("Segoe UI", 11f, FontStyle.Bold);
    private static readonly Font FSub = new("Segoe UI", 8.5f);
    private static readonly Font FHost = new("Segoe UI", 8f);
    private static readonly Font FBig = new("Segoe UI", 19f, FontStyle.Bold);
    private static readonly Font FStat = new("Segoe UI", 8.5f, FontStyle.Bold);
    private static readonly Font FMetric = new("Segoe UI", 8f);

    private CorpStatus _cs = new();
    private List<HistoryPoint> _recent = new();

    public long CorpId => _cs.Corp.Id;

    public CorpCard()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true); // 더블클릭 이벤트 활성
        Width = 320;
        Height = 168;
        Margin = new Padding(8);
        Cursor = Cursors.Hand;
        BackColor = Color.White;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 고DPI에서 카드 크기 스케일(텍스트는 포인트 단위라 자동 스케일 — 상자만 맞춰준다).
        var s = DeviceDpi / 96f;
        if (Math.Abs(s - 1f) > 0.01f)
        {
            Width = (int)(320 * s);
            Height = (int)(168 * s);
        }
    }

    public void SetData(CorpStatus cs, List<HistoryPoint> recent)
    {
        _cs = cs;
        _recent = recent ?? new List<HistoryPoint>();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var corp = _cs.Corp;
        var s = _cs.Latest;
        var status = _cs.Status;
        var color = MainForm.StatusColor(status, corp.Enabled);
        int w = Width, h = Height;
        float ds = DeviceDpi / 96f;              // 고DPI 스케일(텍스트는 포인트라 자동, 좌표만 보정)
        int P(float v) => (int)(v * ds);

        // 카드 배경 + 테두리 + 좌측 상태 스트라이프
        using (var bg = new SolidBrush(corp.Enabled ? Color.White : Color.FromArgb(248, 249, 250)))
            g.FillRectangle(bg, 0, 0, w - 1, h - 1);
        using (var border = new Pen(Color.FromArgb(226, 229, 233)))
            g.DrawRectangle(border, 0, 0, w - 1, h - 1);
        using (var stripe = new SolidBrush(color))
            g.FillRectangle(stripe, 0, 0, P(6), h - 1);

        // 헤더: 법인명 / 리전 / 서버
        using var dark = new SolidBrush(Color.FromArgb(33, 37, 41));
        using var gray = new SolidBrush(Color.FromArgb(134, 142, 150));
        g.DrawString(Ellipsis(g, corp.DisplayName, FName, w - P(120)), FName, dark, P(16), P(10));
        var sub = string.IsNullOrEmpty(corp.Region) ? "Horizon" : $"Horizon · {corp.Region}";
        g.DrawString(Ellipsis(g, sub, FSub, w - P(120)), FSub, gray, P(16), P(33));
        g.DrawString(Ellipsis(g, corp.BaseUrl, FHost, w - P(24)), FHost, gray, P(16), P(52));

        // 우측 상단: 큰 세션 수 + 상태 라벨
        using var statBrush = new SolidBrush(color);
        string big = !corp.Enabled ? "비활성"
            : status == HealthStatus.Down ? "무응답"
            : s != null ? $"{s.SessionTotal:N0}"
            : "—";
        var bigSz = g.MeasureString(big, FBig);
        g.DrawString(big, FBig, statBrush, w - bigSz.Width - P(14), P(8));
        var statText = status == HealthStatus.Down || !corp.Enabled
            ? MainForm.StatusText(status, corp.Enabled)
            : $"세션 · {MainForm.StatusText(status, corp.Enabled)}";
        var stSz = g.MeasureString(statText, FStat);
        g.DrawString(statText, FStat, statBrush, w - stSz.Width - P(14), P(8) + bigSz.Height - 2);

        // 지표 행 1: CS / 게이트웨이 / 팜 · 지표 행 2: 세션호스트 / 문제머신 / 인증서
        if (s != null)
        {
            var m1 = new List<string> { $"CS {s.CsOk}/{s.CsTotal}" };
            if (s.GwTotal > 0) m1.Add($"GW {s.GwOk}/{s.GwTotal}");
            if (s.FarmTotal > 0) m1.Add($"팜 {s.FarmOk}/{s.FarmTotal}");
            if (s.DesktopPools.Count > 0) m1.Add($"풀 {s.DesktopPools.Count}");
            g.DrawString(string.Join("  ·  ", m1), FMetric, gray, P(16), P(74));

            var m2 = new List<string>();
            if (s.RdsTotal > 0) m2.Add($"세션호스트 {s.RdsOk}/{s.RdsTotal}");
            if (s.ProblemMachines.Count > 0) m2.Add($"문제머신 {s.ProblemMachines.Count}");
            var certDays = MinCertDays(s);
            if (certDays is int cd) m2.Add($"인증서 {cd}일");
            m2.Add(AgeText(s.TimestampUtc));
            using var m2Brush = s.ProblemMachines.Count > 0 || HealthEvaluatorHasRdsIssue(s)
                ? new SolidBrush(Color.FromArgb(190, 120, 20)) : new SolidBrush(Color.FromArgb(134, 142, 150));
            g.DrawString(string.Join("  ·  ", m2), FMetric, m2Brush, P(16), P(90));
        }
        else
        {
            g.DrawString(corp.Enabled ? "수집 대기" : "설정에서 주소/계정 입력 후 활성화", FMetric, gray, P(16), P(74));
        }

        // 하단 스파크라인(세션 수 + 상태 색 점)
        DrawSparkline(g, new Rectangle(P(14), P(110), w - P(26), h - P(110) - P(10)), color);
    }

    private static bool HealthEvaluatorHasRdsIssue(CorpSnapshot s)
        => s.RdsServers.Any(HealthEvaluator.RdsProblem);

    /// <summary>표시용 인증서 잔여일 — CS 인증서와 접속 URL 인증서 중 가장 임박한 값.</summary>
    private static int? MinCertDays(CorpSnapshot s)
    {
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

    private void DrawSparkline(Graphics g, Rectangle area, Color baseColor)
    {
        var pts = _recent;
        if (pts.Count == 0)
        {
            using var br = new SolidBrush(Color.FromArgb(173, 181, 189));
            g.DrawString("데이터 없음", FMetric, br, area.X, area.Y + area.Height / 2 - 7);
            return;
        }
        double max = Math.Max(1, pts.Max(p => p.Sessions)) * 1.2;
        int n = pts.Count;
        float dx = n > 1 ? area.Width / (float)(n - 1) : 0;
        float Y(double v) => area.Bottom - (float)(area.Height * Math.Min(v, max) / max);

        // 기준선(격자)
        using (var grid = new Pen(Color.FromArgb(238, 240, 243)))
        {
            g.DrawLine(grid, area.X, area.Y, area.Right, area.Y);
            g.DrawLine(grid, area.X, area.Bottom, area.Right, area.Bottom);
        }

        // 세션 수 연결선
        var linePts = new List<PointF>();
        for (int i = 0; i < n; i++)
            linePts.Add(new PointF(area.X + dx * i, Y(pts[i].Sessions)));
        if (linePts.Count > 1)
        {
            using var pen = new Pen(Color.FromArgb(150, baseColor), 1.4f);
            g.DrawLines(pen, linePts.ToArray());
        }

        // 점(상태 색상) — Down 지점은 세로 붉은 선으로 강조
        for (int i = 0; i < n; i++)
        {
            float x = area.X + dx * i;
            if (pts[i].Status == HealthStatus.Down)
            {
                using var pen = new Pen(Color.FromArgb(90, 214, 60, 60));
                g.DrawLine(pen, x, area.Y, x, area.Bottom);
                continue;
            }
            var c = MainForm.StatusColor(pts[i].Status, true);
            using var br = new SolidBrush(c);
            float r = pts[i].Status == HealthStatus.Up ? 1.8f : 2.6f;
            g.FillEllipse(br, x - r, Y(pts[i].Sessions) - r, r * 2, r * 2);
        }
    }

    private static string AgeText(DateTime utc)
    {
        var s = (DateTime.UtcNow - utc).TotalSeconds;
        if (s < 60) return $"{s:F0}초 전";
        if (s < 3600) return $"{s / 60:F0}분 전";
        if (s < 86400) return $"{s / 3600:F0}시간 전";
        return $"{s / 86400:F0}일 전";
    }

    private static string Ellipsis(Graphics g, string text, Font font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (g.MeasureString(text, font).Width <= maxWidth) return text;
        for (int len = text.Length - 1; len > 1; len--)
        {
            var t = text.Substring(0, len) + "…";
            if (g.MeasureString(t, font).Width <= maxWidth) return t;
        }
        return "…";
    }
}
