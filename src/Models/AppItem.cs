using System;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DashFile.Models;

/// <summary>应用列表的一项（名称暂不解析，用包名代替）</summary>
public partial class AppItem : ObservableObject
{
    public string Package { get; init; } = "";
    public bool IsSystem { get; init; }
    public string ApkPath { get; init; } = "";

    [ObservableProperty] private string _version = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private bool _iconUncertain;
    [ObservableProperty] private bool _isGranted;
    [ObservableProperty] private bool _showSwitch;
    [ObservableProperty] private bool _showActions = true;
    /// <summary>当前 root 实现是否允许直接改授权表；false 时开关置灰但仍显示状态。</summary>
    [ObservableProperty] private bool _canToggleRoot = true;
    [ObservableProperty] private Bitmap? _icon;

    public bool HasIcon => Icon != null;
    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));

    public string Initial =>
        string.IsNullOrEmpty(Package) ? "?" : char.ToUpperInvariant(Package[0]).ToString();

    public IBrush FallbackBrush
    {
        get
        {
            // 用包名哈希稳定地取一个色相，避免每次刷新颜色乱跳
            var h = 0;
            foreach (var c in Package) h = (h * 31 + c) & 0x7FFFFFFF;
            var hue = h % 360;
            return new SolidColorBrush(ColorFromHsl(hue, 0.45, 0.45));
        }
    }

    private static Color ColorFromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
