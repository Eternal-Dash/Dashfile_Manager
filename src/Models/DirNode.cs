using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DashFile.Models;

/// <summary>目录树节点（懒加载子目录，展开状态一直保留）</summary>
public sealed class DirNode
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";

    public bool IsLoaded { get; set; }
    public bool HasChildren { get; set; }
    public bool IsExpanded { get; set; }
    public ObservableCollection<DirNode> Children { get; } = new();
}

/// <summary>
/// 拍平后用于渲染的一行。
/// 所有会变的字段都是可观察的：目录树重建时只原地改值，
///   不再"换一个新对象"——换了对象就必须让 ItemsControl 重建这一行（也就是卡顿的来源）。
/// </summary>
public sealed partial class TreeRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _fullPath = "";
    [ObservableProperty] private int _depth;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _hasChildren;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isPinned;

    /// <summary>拖拽悬停在这个节点上（落点提示）</summary>
    [ObservableProperty] private bool _isDropTarget;

    public DirNode? Node { get; set; }

    public bool ShowSeparator => Depth > 0;

    /// <summary>资源管理器那种"只缩进、不画线"的缩进量</summary>
    public Thickness Indent => new(4 + Depth * 14, 0, 0, 0);

    partial void OnDepthChanged(int value)
    {
        OnPropertyChanged(nameof(ShowSeparator));
        OnPropertyChanged(nameof(Indent));
    }
}
