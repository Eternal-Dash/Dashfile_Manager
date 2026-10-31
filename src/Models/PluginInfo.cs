using System.Collections.ObjectModel;
using Avalonia.Controls;
using DashFile.PluginApi;

namespace DashFile.Models;

/// <summary>插件页左侧列表用的快照模型。</summary>
public sealed class PluginInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>一句话亮点；没有就用 Description 前一小段</summary>
    public string Tagline { get; init; } = "";
    /// <summary>能力徽标（清单里的 tags）</summary>
    public string[] Tags { get; init; } = System.Array.Empty<string>();
    /// <summary>列表图标：一个 emoji 或 1~2 个汉字</summary>
    public string IconText { get; init; } = "◆";
    /// <summary>许可证标识</summary>
    public string License { get; init; } = "";
    public string Homepage { get; init; } = "";
    public string Directory { get; init; } = "";
    public bool Enabled { get; init; }
    public bool IsLoaded { get; init; }
    public bool HasError { get; init; }
    public string StateText { get; init; } = "";
    public string? Error { get; init; }
    public int PageCount { get; init; }
    public int ActionCount { get; init; }

    public ObservableCollection<PluginPageInfo> Pages { get; } = new();

    /// <summary>副行：版本 · 作者 · 状态。作者名完全由插件清单决定，宿主不代填。</summary>
    public string SubLine => string.IsNullOrWhiteSpace(Author)
        ? $"v{Version}   ·   未署名   ·   {StateText}"
        : $"v{Version}   ·   {Author}   ·   {StateText}";

    /// <summary>列表第三行：优先 tagline，其次 description</summary>
    public string Summary => string.IsNullOrWhiteSpace(Tagline) ? Description : Tagline;

    public string BadgeText => IsLoaded ? "已加载" : (HasError ? "错误" : "已禁用");
}

/// <summary>插件提供的一个页面（绑定到“插件”页右侧的页签）。</summary>
public sealed class PluginPageInfo
{
    public string PluginId { get; init; } = "";
    public string PageId { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public PluginPage Source { get; init; } = null!;

    private Control? _view;
    private bool _creating;

    /// <summary>页面控件缓存：切走再切回时保留插件自己的状态。</summary>
    public Control? View
    {
        get => _view;
        set => _view = value;
    }

    public bool IsCreating
    {
        get => _creating;
        set => _creating = value;
    }

    public string DisplayIcon => string.IsNullOrWhiteSpace(Icon) ? "◆" : Icon!;
}
