using System.Threading;
using System.Threading.Tasks;

namespace DashFile.PluginApi;

/// <summary>
/// 所有 DashFile 插件的入口接口。入口类必须有一个公共无参构造函数，
/// 并在 plugin.json 的 entryType 中填写完整类型名。
/// </summary>
public interface IDashFilePlugin
{
    /// <summary>插件元信息（建议与 plugin.json 保持一致）。</summary>
    PluginMetadata Metadata { get; }

    /// <summary>
    /// 插件初始化。宿主在 UI 线程调用；在这里注册页面和动作，或启动后台服务。
    /// 不要在这里做耗时阻塞操作，耗时任务请丢到后台并用 host.Log 报告。
    /// </summary>
    Task InitializeAsync(IPluginHost host, CancellationToken cancellationToken = default);

    /// <summary>插件被禁用 / 重载 / 程序退出时调用。用于取消后台任务、保存状态。</summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 插件基类：省去自己保存 host 的样板代码。
/// 继承后重写 <see cref="OnInitializeAsync"/> 即可。
/// </summary>
public abstract class PluginBase : IDashFilePlugin
{
    protected IPluginHost Host { get; private set; } = null!;

    public abstract PluginMetadata Metadata { get; }

    public Task InitializeAsync(IPluginHost host, CancellationToken cancellationToken = default)
    {
        Host = host;
        return OnInitializeAsync(cancellationToken);
    }

    protected virtual Task OnInitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
