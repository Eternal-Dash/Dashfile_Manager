using System;
using System.Collections.Generic;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DashFile.Models;

/// <summary>主题色预设（设置页里点一下就切换）</summary>
public partial class ThemeOption : ObservableObject
{
    public string Name { get; init; } = "";
    public string Hex { get; init; } = "#A78BFA";

    [ObservableProperty] private bool _isSelected;

    public IBrush Brush => new SolidColorBrush(Color.Parse(Hex));

    public static List<ThemeOption> Presets() => new()
    {
        new ThemeOption { Name = "中性", Hex = "#FFFFFF" },
        new ThemeOption { Name = "淡紫", Hex = "#A78BFA" },
        new ThemeOption { Name = "冰蓝", Hex = "#7DD3FC" },
        new ThemeOption { Name = "薄荷", Hex = "#6EE7B7" },
        new ThemeOption { Name = "暖橙", Hex = "#FDBA74" },
        new ThemeOption { Name = "樱粉", Hex = "#F9A8D4" },
        new ThemeOption { Name = "青柠", Hex = "#BEF264" },
    };
}
