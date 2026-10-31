using Avalonia;

namespace DashFile.Models;

/// <summary>左侧目录树的一行：当前路径的各级祖先 + 当前目录下的子文件夹</summary>
public sealed class PathNode
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public int Depth { get; init; }
    public bool IsCurrent { get; init; }
    public bool IsAncestor { get; init; }

    public Thickness Indent => new Thickness(Depth * 16 + (IsAncestor ? 0 : 6), 0, 0, 0);

    /// <summary>
    /// 面包屑分隔符是否可见（第一级"根目录"前面不要有多余的 ›）。
    /// XAML 里的 crumb 模板绑的就是这个名字；以前这里没有这个属性，
    ///   而工程关着编译绑定（AvaloniaUseCompiledBindingsByDefault=false），绑定失败是静默的，
    ///   于是 IsVisible 走默认 true —— 每一级（含第一级）都画了 ›。
    /// </summary>
    public bool ShowSeparator => Depth > 0;

    /// <summary>目录树箭头用（当前 XAML 未引用，保留给以后的自绘树）</summary>
    public string Glyph => IsAncestor ? "▾" : "▸";
}
