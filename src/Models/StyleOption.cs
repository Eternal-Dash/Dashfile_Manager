using CommunityToolkit.Mvvm.ComponentModel;

namespace DashFile.Models;

/// <summary>界面风格选项</summary>
public partial class StyleOption : ObservableObject
{
    public string Key { get; init; } = "m3";
    public string Name { get; init; } = "";
    public string Hint { get; init; } = "";

    [ObservableProperty] private bool _isSelected;
}
