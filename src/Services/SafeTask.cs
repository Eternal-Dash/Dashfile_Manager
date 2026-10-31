using System;
using System.IO;
using System.Threading.Tasks;

namespace DashFile.Services;

/// <summary>
/// 统一处理"发出去就不管"的后台任务：异常立即写崩溃日志 + 运行日志 + 状态栏，
/// 不再等 GC/finalizer 才由 UnobservedTaskException 报出来（那时已经丢了上下文）。
/// </summary>
public static class SafeTask
{
    public static void Safe(this Task task, string operation, Action<string>? report = null)
        => _ = ObserveAsync(task, operation, report);

    public static void Safe(this Func<Task> factory, string operation, Action<string>? report = null)
    {
        try { _ = ObserveAsync(factory(), operation, report); }
        catch (Exception ex) { Handle(ex, operation, report); }
    }

    private static async Task ObserveAsync(Task task, string operation, Action<string>? report)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { Handle(ex, operation, report); }
    }

    private static void Handle(Exception ex, string operation, Action<string>? report)
    {
        // 取消是正常流程（切目录/关窗口时的预取），不该写崩溃日志也不能报"失败"
        if (ex is OperationCanceledException) return;

        var message = operation + "失败：" + ex.GetBaseException().Message;
        try
        {
            App.WriteCrash("Task:" + operation, ex);
            AppLog.Add("后台任务失败：" + operation + " -> " + ex.GetBaseException().Message);
        }
        catch { }

        if (report == null) return;

        // 这里跑在线程池线程上（ObserveAsync 用了 ConfigureAwait(false)），
        //   而所有调用点都是 m => StatusText = m —— 直接调用会抛"跨线程访问 UI"，
        //   然后被下面的 catch 吞掉，用户永远看不到失败提示。
        try
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                report(message);
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try { report(message); } catch { }
                });
        }
        catch { }
    }
}
