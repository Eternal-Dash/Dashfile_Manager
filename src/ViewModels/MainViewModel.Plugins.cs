using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashFile.Models;
using DashFile.PluginApi;
using DashFile.Services;
using DashFile.Services.Plugins;

namespace DashFile.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // ---------------- 插件 ----------------

    private PluginManager? _plugins;
    private IReadOnlyList<PluginTarget> _pluginSelection = Array.Empty<PluginTarget>();

    public ObservableCollection<PluginInfo> PluginList { get; } = new();
    public ObservableCollection<PluginPageInfo> PluginPages { get; } = new();
    public ObservableCollection<PluginActionBinding> PluginGlobalActions { get; } = new();

    [ObservableProperty] private PluginInfo? _selectedPlugin;
    [ObservableProperty] private PluginPageInfo? _selectedPluginPage;
    [ObservableProperty] private Control? _pluginPageView;
    [ObservableProperty] private string _pluginsStatus = "正在加载插件…";
    [ObservableProperty] private bool _pluginsEmpty = true;
    [ObservableProperty] private string _pluginFolderPath = "";

    public bool HasPluginPages => PluginPages.Count > 0;

    /// <summary>只有一个页面时不显示标签条：它没有切换的意义，只会白占一行高度</summary>
    public bool ShowPluginTabs => PluginPages.Count > 1;

    // ---- 插件页顶部（插件名右侧）由插件推送的副标题 / 徽标 / 状态 ----
    [ObservableProperty] private string _pluginHeaderSubtitle = "";
    [ObservableProperty] private string _pluginHeaderStatus = "";
    public ObservableCollection<string> PluginHeaderBadges { get; } = new();

    public bool HasPluginHeader =>
        PluginHeaderBadges.Count > 0 ||
        !string.IsNullOrWhiteSpace(PluginHeaderSubtitle) ||
        !string.IsNullOrWhiteSpace(PluginHeaderStatus);

    public bool HasPluginSubtitle => !string.IsNullOrWhiteSpace(PluginHeaderSubtitle);
    public bool HasPluginStatus => !string.IsNullOrWhiteSpace(PluginHeaderStatus);

    /// <summary>每个插件各自的顶部信息；切换插件时用它还原</summary>
    private readonly Dictionary<string, (string Subtitle, string Status, List<string> Badges)> _pluginHeaders = new();

    partial void OnSelectedPluginChanged(PluginInfo? value)
    {
        PluginPages.Clear();
        PluginPageView = null;
        if (value != null)
            foreach (var page in value.Pages) PluginPages.Add(page);
        OnPropertyChanged(nameof(HasPluginPages));
        OnPropertyChanged(nameof(ShowPluginTabs));
        RefreshPluginGlobalActions();
        RefreshPluginHeader();
        SelectedPluginPage = PluginPages.FirstOrDefault();
    }

    /// <summary>插件调用 IPluginHost.SetHeader / SetStatus 时进来（可能来自后台线程）</summary>
    internal void ApplyPluginHeader(string pluginId, string? subtitle, IEnumerable<string>? badges,
        string? status, bool onlyStatus = false)
    {
        void Apply()
        {
            var prev = _pluginHeaders.TryGetValue(pluginId, out var p) ? p : ("", "", new List<string>());
            var nextSubtitle = onlyStatus ? prev.Item1 : subtitle ?? "";
            var nextStatus = status ?? "";
            var nextBadges = onlyStatus ? prev.Item3 : (badges?.ToList() ?? new List<string>());
            _pluginHeaders[pluginId] = (nextSubtitle, nextStatus, nextBadges);

            if (SelectedPlugin != null && string.Equals(SelectedPlugin.Id, pluginId, StringComparison.OrdinalIgnoreCase))
                ApplyPluginHeaderToUi(nextSubtitle, nextStatus, nextBadges);
        }

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Apply();
        else Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
    }

    private void RefreshPluginHeader()
    {
        var id = SelectedPlugin?.Id;
        if (id != null && _pluginHeaders.TryGetValue(id, out var p))
        {
            // 插件自己推送过就用它的；否则回落到清单里写的 tagline / tags，
            // 这样作者不写一行 UI 代码也能把“这是什么、有什么能力”说清楚。
            var subtitle = string.IsNullOrWhiteSpace(p.Item1) ? SelectedPlugin?.Tagline ?? "" : p.Item1;
            var badges = p.Item3.Count > 0 ? p.Item3 : (SelectedPlugin?.Tags?.ToList() ?? new List<string>());
            ApplyPluginHeaderToUi(subtitle, p.Item2, badges);
        }
        else
        {
            ApplyPluginHeaderToUi(SelectedPlugin?.Tagline ?? "", "",
                SelectedPlugin?.Tags?.ToList() ?? new List<string>());
        }
    }

    private void ApplyPluginHeaderToUi(string subtitle, string status, List<string> badges)
    {
        PluginHeaderSubtitle = subtitle;
        PluginHeaderStatus = status;
        PluginHeaderBadges.Clear();
        foreach (var b in badges) if (!string.IsNullOrWhiteSpace(b)) PluginHeaderBadges.Add(b);
        OnPropertyChanged(nameof(HasPluginHeader));
        OnPropertyChanged(nameof(HasPluginSubtitle));
        OnPropertyChanged(nameof(HasPluginStatus));
    }

    partial void OnSelectedPluginPageChanged(PluginPageInfo? value)
    {
        if (value == null) { PluginPageView = null; return; }
        if (value.View != null) { PluginPageView = value.View; return; }
        CreatePluginPageViewAsync(value).Safe("创建插件页面");
    }

    private async Task CreatePluginPageViewAsync(PluginPageInfo page)
    {
        if (page.IsCreating) return;
        page.IsCreating = true;
        try
        {
            var descriptor = _plugins?.Find(page.PluginId);
            if (descriptor?.HostObject == null)
            {
                page.View = new TextBlock { Text = "插件未加载，无法打开页面", Margin = new Avalonia.Thickness(16) };
            }
            else
            {
                var view = await page.Source.CreateViewAsync(descriptor.HostObject, CancellationToken.None);
                page.View = view ?? new TextBlock { Text = "插件返回了空页面", Margin = new Avalonia.Thickness(16) };
            }
        }
        catch (Exception ex)
        {
            page.View = new TextBlock
            {
                Text = "插件页面创建失败：" + ex.Message,
                Margin = new Avalonia.Thickness(16),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            LogLine("PLUGIN PAGE " + page.PluginId + " => " + ex.Message);
        }
        finally
        {
            page.IsCreating = false;
            if (ReferenceEquals(SelectedPluginPage, page)) PluginPageView = page.View;
        }
    }

    /// <summary>插件管理器初始化（在窗口显示后执行，不阻塞启动）。</summary>
    private async Task InitializePluginsAsync()
    {
        if (_plugins != null) return;
        try
        {
            var dataRoot = AppPaths.LocalRoot;
            var roots = new List<string> { Path.Combine(AppContext.BaseDirectory, "plugins") };
            roots.Add(AppPaths.Plugins);

            PluginFolderPath = roots[0];
            _plugins = new PluginManager(
                AppVersion,
                roots,
                dataRoot,
                (manifest, instance) => new PluginHostBridge(this, _plugins!, manifest, instance),
                msg => { LogLine(msg); WritePluginHostLog(msg); });
            _plugins.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshPlugins);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await _plugins.LoadAllAsync();
            Perf($"startup plugins {sw.ElapsedMilliseconds}ms count={_plugins.Plugins.Count}");
            RefreshPlugins();
        }
        catch (Exception ex)
        {
            PluginsStatus = "插件加载失败：" + ex.Message;
            LogLine("PLUGIN INIT => " + ex);
        }
    }

    internal void RefreshPlugins()
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(RefreshPlugins);
            return;
        }

        if (_plugins == null)
        {
            PluginsEmpty = true;
            PluginsStatus = "插件系统尚未初始化";
            return;
        }

        var keepPlugin = SelectedPlugin?.Id;
        var keepPage = SelectedPluginPage?.PageId;

        PluginList.Clear();
        foreach (var d in _plugins.Plugins)
        {
            var info = new PluginInfo
            {
                Id = d.Id,
                Name = d.Name,
                Version = d.Manifest.Version,
                Author = string.IsNullOrWhiteSpace(d.Manifest.Authors) ? d.Manifest.Author : d.Manifest.Authors!,
                Description = d.Manifest.Description,
                Tagline = d.Manifest.Tagline ?? "",
                Tags = d.Manifest.Tags ?? System.Array.Empty<string>(),
                IconText = string.IsNullOrWhiteSpace(d.Manifest.Icon) ? "◆" : d.Manifest.Icon!,
                License = d.Manifest.License ?? "",
                Homepage = d.Manifest.Homepage ?? "",
                Directory = d.Directory,
                Enabled = d.Enabled,
                IsLoaded = d.State == PluginLoadState.Loaded,
                HasError = d.State == PluginLoadState.Error,
                StateText = d.StateText,
                Error = d.Error,
                PageCount = d.Pages.Count,
                ActionCount = d.Actions.Count + d.GlobalActions.Count,
            };
            foreach (var page in d.Pages)
                info.Pages.Add(new PluginPageInfo
                {
                    PluginId = d.Id,
                    PageId = page.Id,
                    Title = page.Title,
                    Description = page.Description,
                    Icon = page.Icon,
                    Source = page,
                });
            PluginList.Add(info);
        }

        RefreshNavigationPages();
        PluginsEmpty = PluginList.Count == 0;
        var loaded = PluginList.Count(p => p.IsLoaded);
        PluginsStatus = PluginList.Count == 0
            ? "还没有安装插件 — 把 .dfp 拖进本页，或点「安装 .dfp」"
            : $"共 {PluginList.Count} 个插件 · 已加载 {loaded} 个";

        SelectedPlugin = PluginList.FirstOrDefault(p => p.Id == keepPlugin) ?? PluginList.FirstOrDefault();
        if (SelectedPlugin != null && keepPage != null)
            SelectedPluginPage = PluginPages.FirstOrDefault(p => p.PageId == keepPage) ?? PluginPages.FirstOrDefault();
    }

    [RelayCommand]
    private async Task TogglePluginAsync(PluginInfo? plugin)
    {
        if (plugin == null || _plugins == null) return;
        try
        {
            if (plugin.Enabled) await _plugins.DisableAsync(plugin.Id);
            else await _plugins.EnableAsync(plugin.Id);
            RefreshPlugins();
        }
        catch (Exception ex)
        {
            Toast("插件操作失败：" + ex.Message);
            LogLine("PLUGIN TOGGLE => " + ex);
        }
    }


    // ---------------- 插件安装 / 卸载（.dfp） ----------------

    public async Task<bool> InstallPluginPackageAsync(string path, bool replaceExisting = false)
    {
        if (_plugins == null) await InitializePluginsAsync();
        if (_plugins == null) return false;
        try
        {
            PluginsStatus = "正在安装插件…";
            var installed = await _plugins.InstallPackageAsync(path, replaceExisting);
            RefreshPlugins();
            if (installed != null) SelectedPlugin = PluginList.FirstOrDefault(p => p.Id == installed.Id) ?? SelectedPlugin;
            PluginsStatus = installed?.State == PluginLoadState.Error
                ? "插件已复制，但加载失败：" + installed.Error
                : $"已安装：{installed?.Name ?? System.IO.Path.GetFileName(path)}";
            Toast(PluginsStatus);
            return installed != null;
        }
        catch (Exception ex)
        {
            PluginsStatus = "安装失败：" + ex.Message;
            Toast(PluginsStatus);
            LogLine("PLUGIN INSTALL => " + ex);
            return false;
        }
    }

    public async Task<bool> UninstallPluginAsync(PluginInfo? plugin, bool deleteData)
    {
        if (plugin == null || _plugins == null) return false;
        try
        {
            PluginsStatus = "正在卸载 " + plugin.Name + "…";
            await _plugins.UninstallAsync(plugin.Id, deleteData);
            RefreshPlugins();
            PluginsStatus = "已卸载：" + plugin.Name;
            Toast(PluginsStatus);
            return true;
        }
        catch (Exception ex)
        {
            PluginsStatus = "卸载失败：" + ex.Message;
            Toast(PluginsStatus);
            LogLine("PLUGIN UNINSTALL => " + ex);
            return false;
        }
    }

    [RelayCommand]
    private async Task ReloadPluginsAsync()
    {
        if (_plugins == null) { await InitializePluginsAsync(); return; }
        try
        {
            PluginsStatus = "正在重新加载插件…";
            await _plugins.ReloadAllAsync();
            RefreshPlugins();
            Toast("插件已重新加载");
        }
        catch (Exception ex)
        {
            PluginsStatus = "重新加载失败：" + ex.Message;
            LogLine("PLUGIN RELOAD => " + ex);
        }
    }

    [RelayCommand]
    private void OpenPluginFolder()
    {
        try
        {
            var path = _plugins?.Roots.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "plugins");
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Toast("打开插件目录失败：" + ex.Message); }
    }

    [RelayCommand]
    private void SelectPluginPage(PluginPageInfo? page)
    {
        if (page != null) SelectedPluginPage = page;
    }

    private void RefreshPluginGlobalActions()
    {
        PluginGlobalActions.Clear();
        var descriptor = SelectedPlugin == null ? null : _plugins?.Find(SelectedPlugin.Id);
        if (descriptor == null || descriptor.State != PluginLoadState.Loaded) return;
        foreach (var action in descriptor.GlobalActions)
            PluginGlobalActions.Add(new PluginActionBinding(descriptor, action));
    }

    [RelayCommand]
    private async Task RunPluginGlobalActionAsync(PluginActionBinding? binding)
    {
        if (binding == null) return;
        await ExecutePluginActionAsync(binding, PluginTarget.ForDevice(DevModel));
    }

    public IReadOnlyList<PluginActionBinding> GetPluginActions(PluginTargetKind kind)
        => _plugins?.GetActions(kind) ?? Array.Empty<PluginActionBinding>();

    public IReadOnlyList<PluginActionBinding> GetPluginGlobalActions()
        => _plugins?.GetGlobalActions() ?? Array.Empty<PluginActionBinding>();

    public async Task ExecutePluginActionAsync(PluginActionBinding binding, PluginTarget target)
    {
        if (binding.Action.RequiresRoot && !IsRoot)
        {
            Toast($"插件「{binding.Plugin.Name}」需要 root 权限");
            return;
        }
        if (binding.Action.CanExecute != null && !binding.Action.CanExecute(target))
        {
            Toast($"插件「{binding.Plugin.Name}」当前目标不可用");
            return;
        }

        try
        {
            await binding.Action.ExecuteAsync(target, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Toast($"插件「{binding.Plugin.Name}」执行失败：{ex.Message}");
            LogLine($"PLUGIN ACTION {binding.Plugin.Id}/{binding.Action.Id} => {ex}");
        }
    }


}
