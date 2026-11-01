using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace DashFile.Views;

/// <summary>
/// 右键菜单图标：统一用 16×16 描边路径，按菜单文字自动匹配。
/// 这样所有菜单（文件/目录/应用/模块/插件）都不用逐个写图标。
/// </summary>
internal static class MenuIcons
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["open"]      = "M2.5,3.5 h3.6 l1.5,2 h5.9 a1,1 0 0 1 1,1 v6 a1,1 0 0 1 -1,1 h-11 a1,1 0 0 1 -1,-1 v-8 a1,1 0 0 1 1,-1 z",
        ["newfolder"] = "M2,4.3 h4.2 l1.4,1.6 h6.2 v7.3 h-11.8 z M8,9.2 v3 M6.5,10.7 h3",
        ["newfile"]   = "M4.5,2.5 h5 l2.5,2.5 v8.5 h-7.5 z M9.5,2.5 v3 h2.5 M8,9 v3 M6.5,10.5 h3",
        ["upload"]    = "M8,12.5 V3.5 M4.5,7 L8,3.5 L11.5,7 M3,14 h10",
        ["download"]  = "M8,3.5 V12.5 M4.5,9 L8,12.5 L11.5,9 M3,14 h10",
        ["copy"]      = "M5.5,5.5 h7.5 v7.5 h-7.5 z M3,10.5 v-7.5 h7.5",
        ["paste"]     = "M4.5,4 h7 v9.5 h-7 z M6,4 v-1.5 h4 V4 M6.5,7 h3 M6.5,9.5 h3",
        ["cut"]       = "M2.5,11 a2,2 0 1 0 4,0 a2,2 0 1 0 -4,0 M9.5,11 a2,2 0 1 0 4,0 a2,2 0 1 0 -4,0 M5.8,9.5 l6.7,-7.5 M10.2,9.5 l-6.7,-7.5",
        ["rename"]    = "M3,13 l0.4,-2.8 L11.4,2.2 l2.4,2.4 L5.8,12.6 z M10.6,3 l2.4,2.4",
        ["delete"]    = "M4.5,4.5 h7 M6.5,4.5 v-1.5 h3 v1.5 M5.5,4.5 l0.7,9 h3.6 l0.7,-9 M7,6.5 v5 M9,6.5 v5",
        ["info"]      = "M8,2.2 a5.8,5.8 0 1,0 0,11.6 a5.8,5.8 0 1,0 0,-11.6 M8,7 v4 M8,5.2 v0.6",
        ["lock"]      = "M4.5,7 h7 a1,1 0 0 1 1,1 v5 a1,1 0 0 1 -1,1 h-7 a1,1 0 0 1 -1,-1 v-5 a1,1 0 0 1 1,-1 z M5.5,7 v-2.5 a2.5,2.5 0 0 1 5,0 v2.5 M8,10 v1.5",
        ["edit"]      = "M3,13 h10 M4.5,10.5 l0.3,-2.2 L10.8,2.2 l2.2,2.2 L6.7,10.2 z",
        ["run"]       = "M5,3 l7,5 l-7,5 z",
        ["archive"]   = "M3,5 l5,-2.5 l5,2.5 v6 l-5,2.5 l-5,-2.5 z M3,5 l5,2.5 l5,-2.5 M8,7.5 v6",
        ["extract"]   = "M4,3 h8 v4 h-8 z M4,11 h8 v3 h-8 z M8,7.6 v3.2 M6.6,9.6 L8,11 L9.4,9.6",
        ["tag"]       = "M2.5,7 l5,-4.5 h5 v5 l-4.5,5 z M10.6,5.4 v0.01",
        ["refresh"]   = "M13.5,6 a5.5,5.5 0 1 0 0,4 M13.5,2.5 v3.5 h-3.5",
        ["terminal"]  = "M3,4 l4,4 l-4,4 M9,12 h4",
        ["search"]    = "M7,2.5 a4.5,4.5 0 1,0 0,9 a4.5,4.5 0 1,0 0,-9 M10.5,10.5 l3,3",
        ["shield"]    = "M8,2 l5,2.2 v3.6 l-5,6 l-5,-6 v-3.6 z M5.8,7.8 l2,2 l3.4,-3.4",
        ["code"]      = "M6,4 L2,8 L6,12 M10,4 L14,8 L10,12",
        ["hash"]      = "M6,2.5 L4.5,13.5 M11,2.5 L9.5,13.5 M3,6 h10 M2.5,10 h10",
        ["check"]     = "M3,8.5 l3.2,3.2 L13,5",
        ["plugin"]    = "M2,3.5 h4 v-1 a2,2 0 0 1 4,0 v1 h3.5 v4 h-1 a1.5,1.5 0 0 0 0,3 h1 v3 h-4 v-1 a1.5,1.5 0 0 0 -3,0 v1 h-4.5 z",
        ["list"]      = "M3,4 h10 M3,8 h10 M3,12 h10",
        ["star"]      = "M8,2.5 l1.6,3.4 l3.7,0.5 l-2.7,2.6 l0.7,3.7 L8,10.9 l-3.3,1.8 l0.7,-3.7 L2.7,6.4 l3.7,-0.5 z",
        ["dot"]       = "M8,6.4 a1.6,1.6 0 1,0 0,3.2 a1.6,1.6 0 1,0 0,-3.2",

        // 打开方式：每项一个独立图形
        ["text"]      = "M4.5,1.5 h7 a1,1 0 0 1 1,1 v11 a1,1 0 0 1 -1,1 h-7 a1,1 0 0 1 -1,-1 v-11 a1,1 0 0 1 1,-1 z M6,5 h4 M6,8 h4 M6,11 h2",
        ["image"]     = "M2.5,2.5 h11 a1,1 0 0 1 1,1 v9 a1,1 0 0 1 -1,1 h-11 a1,1 0 0 1 -1,-1 v-9 a1,1 0 0 1 1,-1 z M4,6 a1,1 0 1 0 2,0 a1,1 0 1 0 -2,0 M2,12 l4,-4 2.5,2.5 2,-2 3.5,3.5",
        ["music"]     = "M3,11.5 a2,2 0 1 0 4,0 a2,2 0 1 0 -4,0 M7,11.5 v-8 l6,-1.5 v8 M9,10 a2,2 0 1 0 4,0 a2,2 0 1 0 -4,0 M7,6 l6,-1.5",
        ["video"]     = "M2,4.5 h8.5 v7 h-8.5 z M10.5,7 l3.5,-2 v6 l-3.5,-2",
        ["hex"]       = "M3,2.5 h10 v11 h-10 z M5.4,5.6 h1 M7.8,5.6 h1 M10.2,5.6 h1 M5.4,8 h1 M7.8,8 h1 M10.2,8 h1 M5.4,10.4 h1 M7.8,10.4 h1",
        ["xml"]       = "M6,4 L2.5,8 L6,12 M10,4 L13.5,8 L10,12 M9,3.4 L7,12.6",
        ["dex"]       = "M3,3 h10 v10 h-10 z M5,5.8 h6 M5,8 h6 M5,10.2 h3.6",
        ["arsc"]      = "M3,3 h10 v10 h-10 z M5.4,5.4 h5.2 v5.2 h-5.2 z M6.6,9.4 l1.2,-1.8 1.3,1.4",
        ["apk"]       = "M4,6 h8 v6 h-8 z M4,6 a4,4 0 0 1 8,0 M5,2 l-1,-1 M11,2 l1,-1 M2,7 v4 M14,7 v4 M6,12 v2 M10,12 v2 M6,4 v0.2 M10,4 v0.2",
        ["apks"]      = "M2.4,3.4 h6 v6 h-6 z M6.6,6.6 h7 v7 h-7 z M4.6,6.4 v1.2 M9.6,10.4 v1.2",
        ["font"]      = "M3,13 L7,3 L11,13 M4.6,9.4 h5 M12.6,13 v-4",
        ["sign"]      = "M3,12.6 h10 M4,10.2 c1.8,-4 2.8,-6 3.8,-6 s0.8,3.2 1.8,3.2 1.4,-1.6 2.4,-1.6",
        ["svg"]       = "M8,2 l5,3 v6 l-5,3 l-5,-3 v-6 z M5.4,9 l2,-3 1.6,2 1.4,-1.6 2,2",
        ["nine"]      = "M3,3 h10 v10 h-10 z M5,5 h6 v6 h-6 z",
        ["jar"]       = "M5,2.8 h6 v10.4 h-6 z M7,2.8 v2 M9,2.8 v2 M8,5.8 v2 M7,9 h2",
        ["class"]     = "M5,2.8 h6 v10.4 h-6 z M7,5.8 h2 M7,8 h2 M7,10.2 h2",
    };

    // Bounded host glyph cache. Arbitrary plugin paths are parsed without growing the cache.
    private static readonly IReadOnlyDictionary<string, Geometry> Geometries =
        Paths.Values.Distinct().ToDictionary(path => path, Geometry.Parse);

    public static bool Has(string key) => Paths.ContainsKey(key);

    /// <summary>按菜单文字猜图标（所有菜单共用，避免每个 Add 都手写）。</summary>
    public static string ForHeader(string? header)
    {
        var h = header ?? "";
        if (h.Length == 0) return "dot";
        if (h.Contains("插件", StringComparison.Ordinal)) return "plugin";
        if (h.Contains("签名", StringComparison.Ordinal)) return "shield";
        if (h.Contains("哈希", StringComparison.Ordinal) || h.Contains("校验", StringComparison.Ordinal)) return "hash";
        if (h.Contains("逆向", StringComparison.Ordinal) || h.Contains("分析", StringComparison.Ordinal) ||
            h.Contains("搜索", StringComparison.Ordinal) || h.Contains("查找", StringComparison.Ordinal)) return "search";
        if (h.Contains("解压", StringComparison.Ordinal)) return "extract";
        if (h.Contains("压缩", StringComparison.Ordinal) || h.Contains("打包", StringComparison.Ordinal) ||
            h.Contains("压缩包", StringComparison.Ordinal)) return "archive";
        if (h.Contains("SELinux", StringComparison.Ordinal) || h.Contains("标签", StringComparison.Ordinal)) return "tag";
        if (h.Contains("刷新", StringComparison.Ordinal)) return "refresh";
        if (h.Contains("命令行", StringComparison.Ordinal)) return "terminal";
        if (h.Contains("粘贴", StringComparison.Ordinal)) return "paste";
        if (h.Contains("复制", StringComparison.Ordinal)) return "copy";
        if (h.Contains("剪切", StringComparison.Ordinal)) return "cut";
        if (h.Contains("重命名", StringComparison.Ordinal)) return "rename";
        if (h.Contains("删除", StringComparison.Ordinal) || h.Contains("卸载", StringComparison.Ordinal)) return "delete";
        if (h.Contains("新建文件夹", StringComparison.Ordinal)) return "newfolder";
        if (h.Contains("新建文件", StringComparison.Ordinal)) return "newfile";
        if (h.Contains("上传", StringComparison.Ordinal)) return "upload";
        if (h.Contains("下载", StringComparison.Ordinal) || h.Contains("导出", StringComparison.Ordinal) ||
            h.Contains("提取", StringComparison.Ordinal)) return "download";
        if (h.Contains("属性", StringComparison.Ordinal) || h.Contains("详情", StringComparison.Ordinal)) return "info";
        if (h.Contains("权限", StringComparison.Ordinal)) return "lock";
        if (h.Contains("编辑", StringComparison.Ordinal) || h.Contains("修改", StringComparison.Ordinal)) return "edit";
        if (h.Contains("执行", StringComparison.Ordinal) || h.Contains("运行", StringComparison.Ordinal)) return "run";
        if (h.StartsWith("打开", StringComparison.Ordinal) || h.Contains("进入", StringComparison.Ordinal)) return "open";
        return "dot";
    }

    /// <summary>生成一个菜单图标控件；key 不存在时返回 null。</summary>
    public static Control? Create(string key) => Create(key, null);

    public static Control? Create(string key, IBrush? brush)
        => Paths.TryGetValue(key, out var data) ? CreatePath(data, brush) : null;

    /// <summary>用任意 16×16 描边路径生成图标（插件自带图标用）。</summary>
    public static Control? CreatePath(string? data) => CreatePath(data, null);

    /// <summary>带自定义描边色的图标（打开方式用：每项不同颜色）。</summary>
    public static Control? CreatePath(string? data, IBrush? custom)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        IBrush brush = custom ?? Brushes.Gray;
        if (custom == null)
        {
            try
            {
                if (Application.Current?.TryGetResource("TextSub", null, out var res) == true && res is IBrush b)
                    brush = b;
            }
            catch { }
        }
        return new Avalonia.Controls.Shapes.Path
        {
            Data = Geometries.TryGetValue(data, out var geometry) ? geometry : Geometry.Parse(data),
            Stroke = brush,
            StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }

    public static MenuItem Item(string header, string? iconKey = null)
        => new() { Header = header, Icon = Create(iconKey ?? ForHeader(header)) };
}
