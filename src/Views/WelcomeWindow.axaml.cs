using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DashFile.Services;

namespace DashFile.Views;

/// <summary>
/// 首次启动的引导页：USB 调试怎么开、有 root 时给 shell 放行、作者署名提示。
/// 底部「以后不再显示」默认不勾，勾了并关闭才写永久标识（AppSettings.WelcomeHidden）。
/// </summary>
public partial class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DashFile.Controls.ViewAnimator.AttachWindow(this);
        var ver = this.FindControl<TextBlock>("VerText");
        if (ver != null) ver.Text = "v" + AppInfo.Version;
        var shortcut = this.GetControl<CheckBox>("DesktopShortcutBox");
        shortcut.IsVisible = System.OperatingSystem.IsWindows() && !AppSettings.Current.DesktopShortcutOffered;
        Opened += (_, _) => WindowPlacement.ClampToWorkingArea(this);
    }

    /// <summary>没勾过「不再显示」才弹；已经勾过就直接返回，不打扰。</summary>
    public static async Task ShowOnceAsync(Window owner)
    {
        try
        {
            if (AppSettings.Current.WelcomeHidden) return;
            var dialog = new WelcomeWindow();
            await dialog.ShowDialog(owner);
            var settings = AppSettings.Current;
            if (dialog.GetControl<CheckBox>("DontShowBox").IsChecked == true)
                settings.WelcomeHidden = true;

            var offer = dialog.GetControl<CheckBox>("DesktopShortcutBox");
            if (offer.IsVisible)
            {
                if (offer.IsChecked != true) settings.DesktopShortcutOffered = true;
                else
                {
                    try
                    {
                        await Task.Run(DesktopShortcut.Create);
                        settings.DesktopShortcutOffered = true;
                    }
                    catch (System.Exception ex)
                    {
                        AppLog.Add("创建桌面快捷方式失败：" + ex.Message);
                        if (owner.DataContext is DashFile.ViewModels.MainViewModel vm)
                            vm.Toast("创建桌面快捷方式失败，详情见运行日志");
                    }
                }
            }
            AppSettings.Save();
        }
        catch (System.Exception ex) { AppLog.Add("欢迎页面失败：" + ex.Message); }
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();
}
