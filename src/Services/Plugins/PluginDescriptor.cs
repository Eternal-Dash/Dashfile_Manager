using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using DashFile.PluginApi;

namespace DashFile.Services.Plugins;

public enum PluginLoadState
{
    Disabled = 0,
    Loaded = 1,
    Error = 2,
}

/// <summary>
/// 一个已发现插件的宿主侧状态。UI 只读这里，不直接碰插件实例。
/// </summary>
public sealed class PluginDescriptor
{
    public PluginManifest Manifest { get; }

    /// <summary>插件目录（绝对路径）。</summary>
    public string Directory { get; }

    /// <summary>入口 DLL 的绝对路径。</summary>
    public string AssemblyPath { get; }

    public PluginLoadState State { get; internal set; } = PluginLoadState.Disabled;

    public string? Error { get; internal set; }

    /// <summary>用户是否启用（可能与 State 不同：启用但加载失败时 State=Error）。</summary>
    public bool Enabled { get; internal set; }

    public string Id => Manifest.Id;

    public string Name => string.IsNullOrWhiteSpace(Manifest.Name) ? Manifest.Id : Manifest.Name;

    public IReadOnlyList<PluginPage> Pages => _pages;

    public IReadOnlyList<PluginAction> Actions => _actions;

    public IReadOnlyList<PluginAction> GlobalActions => _globalActions;

    public string StateText => State switch
    {
        PluginLoadState.Loaded => "已加载",
        PluginLoadState.Error => "加载失败",
        _ => "已禁用",
    };

    internal IDashFilePlugin? Instance;
    internal IPluginHost? HostObject;

    /// <summary>插件初始化的生命周期令牌：超时只 Cancel，卸载时才 Dispose</summary>
    internal System.Threading.CancellationTokenSource? InitCts;
    internal PluginLoadContext? LoadContext;
    internal Assembly? Assembly;

    private readonly List<PluginPage> _pages = new();
    private readonly List<PluginAction> _actions = new();
    private readonly List<PluginAction> _globalActions = new();

    public PluginDescriptor(PluginManifest manifest, string directory, string assemblyPath)
    {
        Manifest = manifest;
        Directory = directory;
        AssemblyPath = assemblyPath;
    }

    internal void AddPage(PluginPage page)
    {
        if (page == null) return;
        if (string.IsNullOrWhiteSpace(page.Id)) page = CopyPage(page, "page-" + (_pages.Count + 1));
        _pages.Add(page);
    }

    internal void AddAction(PluginAction action)
    {
        if (action == null) return;
        if (action.ShowInContextMenu) _actions.Add(action);
        else _globalActions.Add(action);
    }

    internal void AddGlobalAction(PluginAction action)
    {
        if (action == null) return;
        _globalActions.Add(action);
    }

    internal void ClearRegistrations()
    {
        _pages.Clear();
        _actions.Clear();
        _globalActions.Clear();
    }

    private static PluginPage CopyPage(PluginPage page, string id) => new()
    {
        Id = id,
        Title = page.Title,
        Description = page.Description,
        Icon = page.Icon,
        CreateViewAsync = page.CreateViewAsync,
        ShowInMainNavigation = page.ShowInMainNavigation,
        NavigationOrder = page.NavigationOrder,
    };
}

/// <summary>右键菜单里的一条插件动作，带插件归属信息。</summary>
public sealed record PluginActionBinding(PluginDescriptor Plugin, PluginAction Action);

/// <summary>插件宿主内部的事件发送口，由 PluginHostBridge 实现。</summary>
internal interface IPluginEventSink
{
    void RaiseDeviceChanged(DeviceSnapshot snapshot);

    void RaiseFileSelection(PluginTarget? target);
}

/// <summary>插件程序集加载上下文：共享 API / Avalonia，其余依赖从插件目录加载。</summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _directory;
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string assemblyPath)
        : base("DashFile.Plugin:" + System.IO.Path.GetFileNameWithoutExtension(assemblyPath), isCollectible: true)
    {
        _directory = System.IO.Path.GetDirectoryName(assemblyPath) ?? "";
        try { _resolver = new AssemblyDependencyResolver(assemblyPath); }
        catch { _resolver = null!; }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 插件与宿主必须共用同一份契约和 UI 程序集，否则 IPluginHost / Control 会是两个不同的类型，
        //   表现为“插件明明实现了接口，宿主却说不认识”。
        if (string.Equals(assemblyName.Name, typeof(IPluginHost).Assembly.GetName().Name,
                StringComparison.OrdinalIgnoreCase))
            return typeof(IPluginHost).Assembly;

        if (assemblyName.Name != null && IsSharedWithHost(assemblyName.Name))
            return null; // 交回默认上下文加载

        if (_resolver != null)
        {
            try
            {
                var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
                if (!string.IsNullOrEmpty(resolved) && System.IO.File.Exists(resolved))
                    return LoadFromAssemblyPath(resolved);
            }
            catch { }
        }

        if (assemblyName.Name != null)
        {
            var local = System.IO.Path.Combine(_directory, assemblyName.Name + ".dll");
            if (System.IO.File.Exists(local)) return LoadFromAssemblyPath(local);
        }
        return null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        try
        {
            var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (!string.IsNullOrEmpty(path)) return LoadUnmanagedDllFromPath(path);
        }
        catch { }
        return 0;
    }

    private static bool IsSharedWithHost(string name)
        => name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("SkiaSharp", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("HarfBuzzSharp", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("MicroCom", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("CommunityToolkit", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "System", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "netstandard", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "mscorlib", StringComparison.OrdinalIgnoreCase);
}
