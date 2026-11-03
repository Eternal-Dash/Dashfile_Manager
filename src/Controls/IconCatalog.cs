using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DashFile.Controls;

/// <summary>Original DashFile outline icons. Shared 16-unit grid; parsed once, never per row.</summary>
public static class IconCatalog
{
    private static readonly IReadOnlyDictionary<string, Geometry> Icons =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["device"] = "M5 1.5h6a1.5 1.5 0 0 1 1.5 1.5v10a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 13V3A1.5 1.5 0 0 1 5 1.5ZM7 12h2",
            ["status"] = "M13.5 7.5V8A5.5 5.5 0 1 1 10 2.9M6 7.5l2 2 5.5-6",
            ["link"] = "M6 1.5v3m4-3v3M4.5 4.5h7V7a3.5 3.5 0 0 1-7 0ZM8 10.5v4",
            ["serial"] = "M2.5 3v10m2-10v10m2-10v10m3-10v10m2-10v10m2-10v10",
            ["tag"] = "M2 8l6-6h5.5v5.5l-6 6ZM10.5 5h.01",
            ["chip"] = "M5 3.5h6a1.5 1.5 0 0 1 1.5 1.5v6a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 11V5A1.5 1.5 0 0 1 5 3.5ZM6 6h4v4H6ZM6 1.5v2m4-2v2M6 12.5v2m4-2v2M1.5 6h2m-2 4h2m9-4h2m-2 4h2",
            ["android"] = "M4 7h8v5H4ZM4 7a4 4 0 0 1 8 0M5 2.5l-1-1m7 1 1-1M2 7v4m12-4v4m-8 1v2m4-2v2M6 5h.01M10 5h.01",
            ["lock"] = "M4.5 7h7A1.5 1.5 0 0 1 13 8.5v4a1.5 1.5 0 0 1-1.5 1.5h-7A1.5 1.5 0 0 1 3 12.5v-4A1.5 1.5 0 0 1 4.5 7ZM5.5 7V4.5a2.5 2.5 0 0 1 5 0V7M8 10v1.5",
            ["box"] = "M2.5 5 8 2l5.5 3v6L8 14l-5.5-3ZM2.5 5 8 8l5.5-3M8 8v6M5.2 3.5l5.5 3",
            ["calendar"] = "M3 3.5h10v10H3ZM5 1.5v4m6-4v4M3 7h10M5.5 9.5h1m3 0h1m-5 2h1",
            ["layers"] = "M8 2 14 5 8 8 2 5ZM2 8l6 3 6-3M2 11l6 3 6-3",
            ["os"] = "M3 2.5h10a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1v-9a1 1 0 0 1 1-1ZM2 6h12M4.5 4.3h.01M6.5 4.3h.01",
            ["power"] = "M8 1.5V7M4.4 3.8a5.5 5.5 0 1 0 7.2 0",
            ["shield"] = "M8 1.5 13 4v4c0 3-5 6.5-5 6.5S3 11 3 8V4ZM5.5 7.5l2 2 3-3.5",
            ["ram"] = "M2 4h12v7H2ZM4.5 6h2v3h-2Zm5 0h2v3h-2ZM4 11v2m4-2v2m4-2v2",
            ["disk"] = "M3.5 2.5h9L14 9v4H2V9ZM2 9h12M10.5 11h1",
            ["battery"] = "M2.5 4h9A1.5 1.5 0 0 1 13 5.5v5a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 1 10.5v-5A1.5 1.5 0 0 1 2.5 4ZM15 6v4M4 6.5v3m3-3v3",
            ["screen"] = "M2 2.5h12v8H2ZM8 10.5v3M5 13.5h6",
            ["cores"] = "M2.5 2.5h4v4h-4Zm7 0h4v4h-4Zm-7 7h4v4h-4Zm7 0h4v4h-4Z",
            ["pin"] = "M5.5 2h5l-1 4 2.5 3H4l2.5-3ZM8 9v5",
            ["folder"] = "M2 4a1 1 0 0 1 1-1h3l2 2h5a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1ZM2 6h12",
            ["puzzle"] = "M2.5 3.5h4v-1a1.5 1.5 0 0 1 3 0v1h4V7h-1a1.5 1.5 0 0 0 0 3h1v3.5h-4v-1a1.5 1.5 0 0 0-3 0v1h-4Z",
            ["refresh"] = "M13 6a5.5 5.5 0 1 0 .3 3.5M13 2.5V6H9.5",
            ["reset"] = "M3 5.5A5.5 5.5 0 1 1 2.5 10M3 2v3.5h3.5M8 5v3l2 1",
            ["up"] = "M8 14V2M3.5 6.5 8 2l4.5 4.5",
            ["download"] = "M8 2v8M4.5 6.5 8 10l3.5-3.5M2.5 10.5v3h11v-3",
            ["upload"] = "M8 10V2M4.5 5.5 8 2l3.5 3.5M2.5 10.5v3h11v-3",
            ["file"] = "M4 1.5h5L13 5v9H3V2.5a1 1 0 0 1 1-1ZM9 1.5V5h4",
            ["text"] = "M4 1.5h5L13 5v9H3V2.5a1 1 0 0 1 1-1ZM9 1.5V5h4M5.5 8h5m-5 2.5h4",
            ["script"] = "M2 3h12v10H2ZM4.5 6l2 2-2 2M8.5 10h3",
            ["image"] = "M2 2.5h12v11H2ZM4.5 5.5h.01M2 11l4-4 3 3 2-2 3 3",
            ["video"] = "M2 3h12v10H2ZM6.5 5.5 10 8l-3.5 2.5Z",
            ["audio"] = "M6.5 11.5a2 2 0 1 1-2-2h2V3l7-1v8M6.5 6l7-1M13.5 10h-2a2 2 0 1 0 2 2Z",
            ["archive"] = "M3 1.5h10v13H3ZM7 2v2h2v2H7v2h2v2H7v2h2",
            ["shortcut"] = "M6 10.5l-1 1a2.5 2.5 0 0 1-3.5-3.5L4 6a2.5 2.5 0 0 1 3.5 0M10 5.5l1-1A2.5 2.5 0 0 1 14.5 8L12 10a2.5 2.5 0 0 1-3.5 0M5.5 9l5-2",
        }.ToDictionary(p => p.Key, p => Geometry.Parse(p.Value), StringComparer.OrdinalIgnoreCase);

    public static Geometry Get(string? kind) => Icons.TryGetValue(kind ?? "file", out var geometry) ? geometry : Icons["file"];
    public static IEnumerable<string> Names => Icons.Keys;
    public static Geometry Device => Get("device");
    public static Geometry Status => Get("status");
    public static Geometry Link => Get("link");
    public static Geometry Serial => Get("serial");
    public static Geometry Tag => Get("tag");
    public static Geometry Chip => Get("chip");
    public static Geometry Android => Get("android");
    public static Geometry Lock => Get("lock");
    public static Geometry Box => Get("box");
    public static Geometry Calendar => Get("calendar");
    public static Geometry Layers => Get("layers");
    public static Geometry Os => Get("os");
    public static Geometry Power => Get("power");
    public static Geometry Shield => Get("shield");
    public static Geometry Ram => Get("ram");
    public static Geometry Disk => Get("disk");
    public static Geometry Battery => Get("battery");
    public static Geometry Screen => Get("screen");
    public static Geometry Cores => Get("cores");
    public static Geometry Pin => Get("pin");
    public static Geometry Folder => Get("folder");
    public static Geometry Puzzle => Get("puzzle");
}

/// <summary>Decorative fixed-grid icon: never measures to the path's irregular bounding box.</summary>
public sealed class AppIcon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<AppIcon, string>(nameof(Kind), "file");
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<AppIcon, IBrush?>(nameof(Foreground), Brushes.Gray);
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    static AppIcon() => AffectsRender<AppIcon>(KindProperty, ForegroundProperty);
    public AppIcon() { Width = Height = 16; IsHitTestVisible = false; }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 16;
        if (scale <= 0 || Foreground is null) return;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - 16 * scale) / 2, (Bounds.Height - 16 * scale) / 2)))
            context.DrawGeometry(null, new Pen(Foreground, 1.35, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), IconCatalog.Get(Kind));
    }
}
