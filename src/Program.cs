using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using DashFile.Services;

namespace DashFile;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // 单实例：第二次双击只激活已有窗口，避免两个进程抢 adb server / 图标缓存 / settings.json
        _mutex = new Mutex(true, "Local\\DashFile.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            ActivateExisting();
            return;
        }

        try
        {
            AppLog.Add("应用启动 v" + AppInfo.Version);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            App.WriteCrash("Main", ex);
            throw;
        }
        finally
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
    }

    private static void ActivateExisting()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("DashFile"))
            {
                if (p.Id == Environment.ProcessId) continue;
                var h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                ShowWindow(h, 9);            // SW_RESTORE
                SetForegroundWindow(h);
                return;
            }
        }
        catch { }
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
