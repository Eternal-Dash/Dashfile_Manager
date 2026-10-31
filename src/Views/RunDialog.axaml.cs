using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DashFile.Views;

public partial class RunDialog : Window
{
    private Func<string, bool, Task<string>>? _exec;
    private Func<int, Task>? _sendKey;
    private string _filePath = "";

    public RunDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DashFile.Controls.ViewAnimator.AttachWindow(this);
        Opened += (_, _) => WindowPlacement.ClampToWorkingArea(this);
    }

    public static async Task ShowAsync(Window owner, string filePath, string initialCommand,
        bool canRoot, bool defaultRoot, Func<string, bool, Task<string>> exec,
        Func<int, Task>? sendKey = null)
    {
        var dlg = new RunDialog();
        dlg._exec = exec;
        dlg._sendKey = sendKey;
        dlg._filePath = filePath;
        dlg.GetControlC<TextBox>("CmdBox").Text = initialCommand;
        var root = dlg.GetControlC<CheckBox>("UseRootBox");
        root.IsChecked = canRoot && defaultRoot;
        root.IsEnabled = canRoot;
        await dlg.ShowDialog(owner);
    }

    private T GetControlC<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private void OnFillSh(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_filePath)) return;
        GetControlC<TextBox>("CmdBox").Text = "sh " + Quote(_filePath);
    }

    private void OnFillLs(object? sender, RoutedEventArgs e)
        => GetControlC<TextBox>("CmdBox").Text = "ls -lZ .";

    private void OnFillPerm(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_filePath)) return;
        GetControlC<TextBox>("CmdBox").Text = "ls -lZ " + Quote(_filePath);
    }

    private void OnClear(object? sender, RoutedEventArgs e)
        => GetControlC<TextBox>("OutBox").Text = "";

    private async void OnVolDown(object? sender, RoutedEventArgs e) => await SendKeyAsync(25, "音量−");
    private async void OnVolUp(object? sender, RoutedEventArgs e) => await SendKeyAsync(24, "音量＋");
    private async void OnPower(object? sender, RoutedEventArgs e) => await SendKeyAsync(26, "电源");

    private async Task SendKeyAsync(int code, string label)
    {
        if (_sendKey == null) return;
        try
        {
            await _sendKey(code);
            GetControlC<TextBox>("OutBox").Text += "· 已发送 " + label + Environment.NewLine;
        }
        catch (Exception ex)
        {
            GetControlC<TextBox>("OutBox").Text += "· 发送失败：" + ex.Message + Environment.NewLine;
        }
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        if (_exec == null) return;
        var cmd = GetControlC<TextBox>("CmdBox").Text ?? "";
        if (string.IsNullOrWhiteSpace(cmd)) return;

        var useRoot = GetControlC<CheckBox>("UseRootBox").IsChecked == true;
        var btn = GetControlC<Button>("RunBtn");
        var state = GetControlC<TextBlock>("StateText");
        var outBox = GetControlC<TextBox>("OutBox");

        btn.IsEnabled = false;
        state.Text = useRoot ? "正在以 root 执行…" : "正在以普通权限执行…";
        outBox.Text = (outBox.Text ?? "") + "\n$ " + cmd + "\n";

        try
        {
            var result = await _exec(cmd, useRoot);
            outBox.Text += result;
            state.Text = "执行完成";
        }
        catch (Exception ex)
        {
            outBox.Text += "执行出错：" + ex.Message;
            state.Text = "执行出错";
        }
        finally
        {
            btn.IsEnabled = true;
            outBox.CaretIndex = outBox.Text?.Length ?? 0;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
