using Avalonia.Controls;
using DashFile.PluginApi;

namespace FastbootFlashPlugin;

public sealed class FlashPlugin : PluginBase, IPluginOperationGuard
{
    private FlashViewModel? _session;
    public bool IsOperationActive => _session?.IsBusy == true;
    public string OperationDescription => "分区刷写 / 镜像回读";
    public override PluginMetadata Metadata => new() { Id = "com.dashfile.fastbootflash", Name = "DashFile - 分区刷写", Version = "1.0.2", Author = "EternalDash", Description = "分区刷写与可验证备份" };

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        _session = new FlashViewModel(Host, new FlashEngine(new NativeToolRunner()));

        // 详情页：只说明"我在跑、入口在哪、我是什么"，不放任何操作控件。
        // 宿主会优先显示非导航页，所以插件管理页点进来看到的就是这一页。
        Host.AddPage(new PluginPage
        {
            Id = "about", Title = "插件信息", ShowInMainNavigation = false,
            Description = "运行状态与插件信息", Icon = "ⓘ",
            CreateViewAsync = (_, _) => Task.FromResult<Control>(new InfoPage())
        });

        // 工作页：进顶部导航，所有操作都在这里，详情页不再重复挂一份。
        Host.AddPage(new PluginPage
        {
            Id = "partition-flash", Title = "分区刷写", ShowInMainNavigation = true, NavigationOrder = 100,
            Description = "分区刷写 / 原生回读 / ADB Root 备份 / 固件包预览", Icon = "▣",
            CreateViewAsync = (_, _) => Task.FromResult<Control>(new FlashPage { DataContext = _session })
        });

        return Task.CompletedTask;
    }

    public override async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_session != null) await _session.StopAsync();
        _session = null;
    }
}
