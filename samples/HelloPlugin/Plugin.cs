using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DashFile.PluginApi;

namespace HelloPlugin;

/// <summary>
/// 最小但完整的 DashFile 插件示例：
///  - 一个自定义页面；
///  - 文件右键动作；
///  - 应用右键动作；
///  - 插件页里的全局动作。
///
/// 真正的逆向 / AI 插件也是同一套入口，只是页面里放自己的 UI 和逻辑。
/// 想改成自己的插件：改 plugin.json 的 id / name / author，再替换 BuildPage 里的内容即可。
/// </summary>
public sealed class HelloPlugin : PluginBase
{
    public override PluginMetadata Metadata => new()
    {
        Id = "com.dashfile.hello",
        Name = "DashFile - Hello 示例",
        Version = "1.0.0",
        Author = "your_name",
        Description = "如你所见，这只是一个实例插件，没什么用捏......",
    };

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        Host.AddPage(PluginPage.FromControl(
            "main", "示例",
            host => BuildPage(host),
            null, "👋"));

        Host.AddAction(new PluginAction
        {
            Id = "file-size",
            Title = "Hello：查看文件大小",
            Description = "演示文件右键动作",
            Targets = new[] { PluginTargetKind.File },
            ExecuteAsync = async (target, ct) =>
            {
                var r = await Host.Device.ShellAsync(
                    "stat -c %s " + PluginShell.Quote(target.Path) + " 2>/dev/null", true, ct);
                var size = r.StdOut.Trim();
                await Host.Ui.ShowMessageAsync("文件大小",
                    $"{target.Path}\n\n{size} 字节", ct);
            },
        });

        Host.AddAction(new PluginAction
        {
            Id = "app-info",
            Title = "Hello：查看应用包名",
            Targets = new[] { PluginTargetKind.App },
            ExecuteAsync = async (target, ct) =>
                await Host.Ui.ShowMessageAsync("应用信息",
                    $"{target.DisplayName}\n\n包名：{target.Package}", ct),
        });

        Host.AddGlobalAction(new PluginAction
        {
            Id = "device-info",
            Title = "显示设备信息",
            ExecuteAsync = async (_, ct) =>
            {
                var d = Host.Device;
                await Host.Ui.ShowMessageAsync("设备信息",
                    $"已连接：{d.IsConnected}\nRoot：{d.IsRoot}\n" +
                    $"型号：{d.Model}\nAndroid：{d.AndroidVersion}\n" +
                    $"Root 实现：{d.RootManagerName}", ct);
            },
        });

        return Task.CompletedTask;
    }

    private static Control BuildPage(IPluginHost host)
    {
        var text = new TextBlock
        {
            Text = "如你所见，这只是一个实例插件，没什么用捏......",
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.75,
        };

        return new Border
        {
            Padding = new Thickness(24),
            Child = text,
        };
    }
}
