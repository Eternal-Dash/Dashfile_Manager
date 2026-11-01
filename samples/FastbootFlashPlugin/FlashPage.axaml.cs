using Avalonia.Controls;
using Avalonia.Markup.Xaml;
namespace FastbootFlashPlugin;
public sealed partial class FlashPage : UserControl
{
    public FlashPage()
    {
        AvaloniaXamlLoader.Load(this);
        // 插件管理页那一栏比主导航页矮，这里保证工作区有可用的最小高度，
        // 由外层 ScrollViewer / 容器负责滚动。
        // 注意：必须判空——XAML 里改了结构却忘了同步这个名字时，原来的 ! 会变成运行时 NRE。
        var workspace = this.FindControl<Grid>("Workspace");
        if (workspace != null)
            SizeChanged += (_, _) => workspace.MinHeight = Math.Max(580, Bounds.Height);
    }
}
