using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace HorizonServiceMonitor;

internal static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main(string[] args)
    {
        // 단일 인스턴스 — 중복 실행 방지(트레이에 이미 상주 중이면 새로 뜨지 않게).
        _singleInstance = new Mutex(true, @"Global\HorizonServiceMonitor.SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("Horizon Service Monitor가 이미 실행 중입니다.\n트레이 아이콘을 확인하세요.",
                "Horizon Service Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 예기치 못한 예외를 로그로 남기고 최대한 계속 실행(트레이 상주 특성).
        Application.ThreadException += (_, e) => Log("ThreadException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("UnhandledException", e.ExceptionObject as Exception);

        string? dbPath = null;
        bool startHidden = false;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i].ToLowerInvariant();
            if (a is "--hidden" or "-h" or "/hidden") startHidden = true;
            else if ((a == "--db" || a == "/db") && i + 1 < args.Length) dbPath = args[++i];
        }

        try
        {
            Application.Run(new TrayAppContext(dbPath, startHidden));
        }
        finally
        {
            try { _singleInstance?.ReleaseMutex(); } catch { /* ignore */ }
        }
    }

    internal static void Log(string kind, Exception? ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(Database.DefaultDbPath())!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:u}] {kind}: {ex}\r\n");
        }
        catch { /* ignore */ }
    }
}
