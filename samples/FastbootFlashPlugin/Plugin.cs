using Avalonia.Controls;
using DashFile.PluginApi;

namespace FastbootFlashPlugin;

public sealed class FlashPlugin : PluginBase, IPluginOperationGuard
{
    private FlashViewModel? _session;
    public bool IsOperationActive => _session?.IsBusy == true;
    public string OperationDescription => "分区刷写 / 镜像回读";
    public override PluginMetadata Metadata => new() { Id = "com.dashfile.fastbootflash", Name = "Fastboot刷写", Version = "1.0.1", Author = "EternalDash", Description = "分区刷写与可验证备份" };
    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        _session = new FlashViewModel(Host, new FlashEngine(new NativeToolRunner()));
        Host.AddPage(new PluginPage
        {
            Id = "partition-flash", Title = "分区刷写", ShowInMainNavigation = true, NavigationOrder = 100,
            Description = "Fastboot刷写 / 原生回读 / ADB Root备份 / 固件包预览", Icon = "▣",
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
