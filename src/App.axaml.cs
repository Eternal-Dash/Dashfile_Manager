using System;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DashFile.Services;
using DashFile.ViewModels;
using DashFile.Views;

namespace DashFile;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        HookExceptions();
        CleanupOldLogs(7);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>兜底：任何未捕获异常都写 Log\crash-日期.log，不让程序直接消失</summary>
    private void HookExceptions()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrash("AppDomain", e.ExceptionObject as Exception);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                WriteCrash("UI", e.Exception);
                e.Handled = true;   // 界面线程异常不再终止进程
            };
        }

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash("Task", e.Exception);
            e.SetObserved();
        };
    }

    internal static void WriteCrash(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Log");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd}.log");

            var sb = new StringBuilder();
            sb.AppendLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] [" + source + "]");
            sb.AppendLine("App: " + AppInfo.Product + " v" + AppInfo.Version + "  (" +
                          System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + ")");
            sb.AppendLine("OS : " + Environment.OSVersion + "  x64=" + Environment.Is64BitProcess);
            sb.AppendLine("CWD: " + Environment.CurrentDirectory);
            var recent = AppLog.Snapshot(20);
            sb.AppendLine("--- 最近操作（" + recent.Length + "）---");
            foreach (var line in recent) sb.AppendLine("  " + line);
            sb.AppendLine("--- exception ---");
            sb.AppendLine(ex?.ToString() ?? "(null)");
            sb.AppendLine();

            File.AppendAllText(file, sb.ToString(), new UTF8Encoding(false));
        }
        catch { /* 日志失败也不能再抛 */ }
    }

    /// <summary>崩溃日志按天保留 N 天，避免越积越多（启动时跑一次）。</summary>
    internal static void CleanupOldLogs(int days)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Log");
            if (!Directory.Exists(dir)) return;
            var limit = DateTime.Now.AddDays(-Math.Max(1, days));
            foreach (var f in Directory.GetFiles(dir, "crash-*.log"))
            {
                try { if (File.GetLastWriteTime(f) < limit) File.Delete(f); } catch { }
            }
        }
        catch { }
    }
}
