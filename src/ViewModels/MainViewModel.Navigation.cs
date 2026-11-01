using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DashFile.Models;

namespace DashFile.ViewModels;

public partial class MainViewModel
{
    public bool HasActivePluginOperations => _plugins?.HasActiveOperations == true || FileTransfers.ActiveCount > 0;

    public ObservableCollection<PluginPageInfo> NavigationPages { get; } = new();
    [ObservableProperty] private Control? _navigationPageView;
    private int _navigationGeneration;
    private readonly Dictionary<PluginPageInfo, Task<Control>> _navigationViews = new();

    private void RefreshNavigationPages()
    {
        var selected = IsNavigationTab ? NavigationPages[SelectedTab - 6] : null;
        var previous = NavigationPages.ToArray();
        var pages = PluginList.Where(p => p.IsLoaded).SelectMany(p => p.Pages)
            .Where(p => p.Source.ShowInMainNavigation).OrderBy(p => p.Source.NavigationOrder)
            .ThenBy(p => p.PluginId, StringComparer.Ordinal).ThenBy(p => p.PageId, StringComparer.Ordinal).ToArray();
        NavigationPages.Clear();
        foreach (var page in pages)
            NavigationPages.Add(previous.FirstOrDefault(old => old.PluginId == page.PluginId && old.PageId == page.PageId && ReferenceEquals(old.Source, page.Source)) ?? page);
        foreach (var removed in _navigationViews.Keys.Where(p => !NavigationPages.Contains(p)).ToArray()) _navigationViews.Remove(removed);
        ++_navigationGeneration;
        if (selected != null)
        {
            var replacement = NavigationPages.FirstOrDefault(p => p.PluginId == selected.PluginId && p.PageId == selected.PageId);
            if (replacement == null) { NavigationPageView = null; SelectedTab = 4; }
            else { SelectedTab = 6 + NavigationPages.IndexOf(replacement); _ = CreateNavigationViewAsync(); }
        }
        OnPropertyChanged(nameof(NavigationPages));
        OnPropertyChanged(nameof(NavigationIndex));
        OnPropertyChanged(nameof(IsNavigationTab));
    }

    private async Task CreateNavigationViewAsync()
    {
        if (!IsNavigationTab) return;
        var page = NavigationPages[SelectedTab - 6];
        var generation = ++_navigationGeneration;
        NavigationPageView = null;
        try
        {
            if (page.View == null)
            {
                var host = _plugins?.Find(page.PluginId)?.HostObject;
                if (host == null) return;
                if (!_navigationViews.TryGetValue(page, out var creation))
                    _navigationViews[page] = creation = page.Source.CreateViewAsync(host, default);
                try { page.View = await creation; }
                finally { _navigationViews.Remove(page); }
            }
            if (generation == _navigationGeneration && IsNavigationTab && ReferenceEquals(NavigationPages[SelectedTab - 6], page))
                NavigationPageView = page.View;
        }
        catch (Exception ex) { StatusText = "插件导航页加载失败：" + ex.Message; }
    }
}
