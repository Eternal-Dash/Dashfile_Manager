using System.Threading;
using System.Threading.Tasks;
using DashFile.PluginApi;

namespace AvbStudioPlugin;

public sealed class AvbPlugin : PluginBase
{
    public override PluginMetadata Metadata => new()
    {
        Id = "com.dashfile.avbstudio",
        Name = "DashFile - AVB 工作台",
        Version = "1.0.0",
        Author = "DashFile",
        Description = "AVB 镜像解析、签名、VBMeta 构建、A/B 批处理与扫描。",
    };

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        Host.AddPage(PluginPage.FromControl(
            "avb-workbench",
            "DashFile - AVB 工作台",
            host => new AvbPage(host),
            "解析 / 重签 AVB 镜像，构建 vbmeta，批量处理与扫描。",
            "AVB"));

        // 标题卡片里的信息挪到宿主的插件页顶部（插件不再在自己的页面里画大标题）
        Host.SetHeader(new PluginHeaderInfo
        {
            Subtitle = "Android Verified Boot 镜像签名 / 解析 / 批处理",
            Badges = new[] { "AOSP testkey 内置", "无 Python 依赖", "GPL-3.0-or-later" },
            Status = "就绪 · 纯 C#",
        });

        return Task.CompletedTask;
    }
}
