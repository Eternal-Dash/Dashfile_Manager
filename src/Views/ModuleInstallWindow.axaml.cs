using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace DashFile.Views;

public partial class ModuleInstallWindow : Window
{
    private Func<Action<string>, CancellationToken, Task>? _job;
    private Func<int, Task>? _sendKey;

    /// <summary>中止信号：关窗时取消，安装流程（上传/ksud 执行）会带着它退出</summary>
    private readonly CancellationTokenSource _cts = new();
    private bool _finished;

    public ModuleInstallWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DashFile.Controls.ViewAnimator.AttachWindow(this);
        Closing += OnClosingWhileRunning;
        Closed += (_, _) => { try { _cts.Dispose(); } catch { } };
    }

    public static async Task ShowAsync(Window owner, string fileName,
        Func<Action<string>, CancellationToken, Task> job, Func<int, Task> sendKey)
    {
        var w = new ModuleInstallWindow { Title = "刷入模块 · " + fileName };
        w._job = job;
        w._sendKey = sendKey;
        w.FindControl<TextBlock>("FileText")!.Text = fileName;
        w.Opened += async (_, _) => await w.RunAsync();
        await w.ShowDialog(owner);
    }

    /// <summary>
    /// 以前这个窗口可以随时被关掉，而安装 job 还在跑；外层是 foreach 刷多个模块，
    ///   用户关掉窗口后下一个 adb install 立刻开始 —— 两个安装抢同一台设备。
    ///   现在关窗 = 取消，而且要等 job 真的结束才关（带兜底超时，绝不能把窗口卡死）。
    /// </summary>
    private async void OnClosingWhileRunning(object? sender, WindowClosingEventArgs e)
    {
        if (_finished) return;

        e.Cancel = true;
        var state = this.FindControl<TextBlock>("StateText");
        if (state != null) state.Text = "正在中止安装…";
        try { _cts.Cancel(); } catch { }

        // 给安装流程一点时间退出；超时就直接放行，避免用户关不掉窗口
        for (var i = 0; i < 40 && !_finished; i++) await Task.Delay(100);
        _finished = true;
        Close();
    }

    private void Append(string line)
        => Dispatcher.UIThread.Post(() =>
        {
            var box = this.FindControl<TextBox>("LogBox")!;
            box.Text = (box.Text ?? "") + line + "\n";
            box.CaretIndex = box.Text.Length;
        });

    private async Task RunAsync()
    {
        var state = this.FindControl<TextBlock>("StateText")!;
        var close = this.FindControl<Button>("CloseBtn")!;
        state.Text = "正在安装…";
        try
        {
            if (_job != null) await _job(Append, _cts.Token);
            state.Text = _cts.IsCancellationRequested ? "已中止安装" : "安装流程已结束";
        }
        catch (OperationCanceledException)
        {
            Append("× 已中止");
            state.Text = "已中止安装";
        }
        catch (Exception ex)
        {
            Append("执行异常：" + ex.Message);
            state.Text = "执行异常";
        }
        finally
        {
            _finished = true;
            close.IsEnabled = true;
        }
    }

    private async void OnVolDown(object? sender, RoutedEventArgs e) { if (_sendKey != null) await _sendKey(25); Append("· 已发送 音量-"); }
    private async void OnVolUp(object? sender, RoutedEventArgs e) { if (_sendKey != null) await _sendKey(24); Append("· 已发送 音量+"); }
    private async void OnPower(object? sender, RoutedEventArgs e) { if (_sendKey != null) await _sendKey(26); Append("· 已发送 电源"); }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
