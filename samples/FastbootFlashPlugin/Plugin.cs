using Avalonia.Controls;
using DashFile.PluginApi;
using System.Text.Json;

namespace FastbootFlashPlugin;

public sealed class FlashPlugin : PluginBase, IPluginOperationGuard
{
    private FlashViewModel? _session;
    public bool IsOperationActive => _session?.IsBusy == true;
    public string OperationDescription => "分区刷写 / 镜像回读";
    private static readonly PluginManifest Manifest = ReadManifest();

    public override PluginMetadata Metadata => new()
    {
        Id = Manifest.Id, Name = Manifest.Name, Version = Manifest.Version,
        Author = Manifest.Author, Description = Manifest.Description
    };

    private static PluginManifest ReadManifest()
    {
        using var stream = typeof(FlashPlugin).Assembly.GetManifestResourceStream("FastbootFlashPlugin.plugin.json")
            ?? throw new InvalidOperationException("缺少内嵌插件清单");
        return JsonSerializer.Deserialize<PluginManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("插件清单为空");
    }

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        _session = new FlashViewModel(Host, new FlashEngine(new NativeToolRunner()));

        // 详情页：只说明"我在跑、入口在哪、我是什么"，不放任何操作控件。
        // 宿主会优先显示非导航页，所以插件管理页点进来看到的就是这一页。
        Host.AddPage(new PluginPage
        {
            Id = "about", Title = "插件信息", ShowInMainNavigation = false,
            Description = "运行状态与插件信息", Icon = "ⓘ",
            CreateViewAsync = (_, _) => Task.FromResult<Control>(new InfoPage
            {
                DataContext = new
                {
                    Manifest.Id, Manifest.Name, Manifest.Version, Manifest.Author, Manifest.License,
                    Compatibility = string.IsNullOrWhiteSpace(Manifest.MinHostVersion)
                        ? $"v{Manifest.ApiVersion}"
                        : $"v{Manifest.ApiVersion}（宿主 {Manifest.MinHostVersion} 及以上）"
                }
            })
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
