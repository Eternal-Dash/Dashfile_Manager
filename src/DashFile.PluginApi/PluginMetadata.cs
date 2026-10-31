namespace DashFile.PluginApi;

/// <summary>
/// 插件 API 的版本信息。宿主与插件通过 <see cref="Version"/> 判断兼容性：
/// 插件声明的 apiVersion 大于宿主支持的版本时，宿主会拒绝加载并提示升级 DashFile。
/// </summary>
public static class PluginApiInfo
{
    /// <summary>当前插件 API 主版本。破坏性改动才递增，新增接口用新类型/默认实现。</summary>
    public const int Version = 1;

    /// <summary>宿主产品名。</summary>
    public const string HostProduct = "DashFile";

    /// <summary>插件清单文件名。</summary>
    public const string ManifestFileName = "plugin.json";
}

/// <summary>
/// 插件自报的元信息。宿主会优先使用 plugin.json 里的信息做展示，
/// 插件实现 <see cref="IDashFilePlugin.Metadata"/> 返回的元信息用于运行期自检。
/// </summary>
public sealed class PluginMetadata
{
    /// <summary>全局唯一 id，建议用反向域名，例如 com.example.dexhelper。</summary>
    public string Id { get; init; } = "";

    /// <summary>展示名。</summary>
    public string Name { get; init; } = "";

    /// <summary>插件版本，例如 1.0.0。</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>作者。</summary>
    public string Author { get; init; } = "";

    /// <summary>一句话说明。</summary>
    public string Description { get; init; } = "";

    /// <summary>展示用的亮点短语（可选）。插件页列表与详情页优先用它，比 description 短。</summary>
    public string? Tagline { get; init; }

    /// <summary>多作者写法（可选），例如 "甲 / 乙"；为空时用 <see cref="Author"/>。</summary>
    public string? Authors { get; init; }

    /// <summary>版本序号（可选），用于比较新旧，例如 1002003 表示 1.2.3。</summary>
    public int VersionCode { get; init; }

    /// <summary>许可证标识（可选），例如 GPL-3.0-or-later。</summary>
    public string? License { get; init; }

    /// <summary>能力徽标（可选），显示在插件页头部，例如 "无 Python 依赖"。</summary>
    public string[]? Tags { get; init; }

    /// <summary>列表图标（可选），一个 emoji 或 1~2 个汉字。</summary>
    public string? Icon { get; init; }

    /// <summary>主页 / 仓库地址（可选）。</summary>
    public string? Homepage { get; init; }
}

/// <summary>
/// plugin.json 的宿主侧映射。插件作者只需要照文档填写，不需要引用这个类型。
/// </summary>
public sealed class PluginManifest
{
    /// <summary>全局唯一 id（必填）。</summary>
    public string Id { get; set; } = "";

    /// <summary>展示名（必填）。</summary>
    public string Name { get; set; } = "";

    /// <summary>插件版本。</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>作者名（必填，插件页会原样展示）。</summary>
    public string Author { get; set; } = "";

    /// <summary>多作者写法（可选），例如 "甲 / 乙"；填了就优先显示它。</summary>
    public string? Authors { get; set; }

    public string Description { get; set; } = "";

    /// <summary>一句话亮点（可选），比 description 短，用于列表与详情页副标题。</summary>
    public string? Tagline { get; set; }

    /// <summary>版本序号（可选），便于判断新旧：1.2.3 → 1002003。</summary>
    public int VersionCode { get; set; }

    /// <summary>许可证标识（可选），例如 GPL-3.0-or-later。</summary>
    public string? License { get; set; }

    /// <summary>能力徽标（可选），显示在插件页头部。</summary>
    public string[]? Tags { get; set; }

    /// <summary>列表图标（可选），一个 emoji 或 1~2 个汉字。</summary>
    public string? Icon { get; set; }

    public string? Homepage { get; set; }

    /// <summary>入口程序集文件名（相对于插件目录），例如 HelloPlugin.dll。</summary>
    public string Assembly { get; set; } = "";

    /// <summary>入口类型全名（含命名空间），必须实现 IDashFilePlugin。</summary>
    public string EntryType { get; set; } = "";

    /// <summary>插件编译时使用的 API 版本。</summary>
    public int ApiVersion { get; set; } = PluginApiInfo.Version;

    /// <summary>要求的最低宿主版本（可选），例如 1.0.0。</summary>
    public string? MinHostVersion { get; set; }

    /// <summary>默认是否启用；用户在插件页关掉后，状态写在本机配置里，不修改清单。</summary>
    public bool Enabled { get; set; } = true;
}
