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
        Opened += (_, _) => WindowPlacement.ClampToWorkingArea(this);
    }

    /// <summary>没勾过「不再显示」才弹；已经勾过就直接返回，不打扰。</summary>
    public static async Task ShowOnceAsync(Window owner)
    {
        try
        {
            if (AppSettings.Current.WelcomeHidden) return;
            await new WelcomeWindow().ShowDialog(owner);
        }
        catch { }
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (this.FindControl<CheckBox>("DontShowBox")?.IsChecked == true)
            {
                var st = AppSettings.Current;
                st.WelcomeHidden = true;
                AppSettings.Save();
            }
        }
        catch { }
        Close();
    }
}
