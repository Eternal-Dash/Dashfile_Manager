using System;
using Avalonia;
using Avalonia.Media;

namespace DashFile.Services;

/// <summary>
/// 主题 + 风格。
/// m3   = Material You：表面由主题色轻微染色，12px 圆角，紧凑行高。
/// miui = HyperOS/MIUIx：中性灰面（不染色）、靠亮度分层、几乎无描边、20px 大圆角、行高更松。
/// </summary>
public static class ThemeService
{
    public static string Style { get; private set; } = "m3";

    public static void ApplyStyle(string style, string hex)
    {
        // 只有 标准(m3) / MIUI 两种；玻璃风格已整体移除（历史配置里的 glass 会归位到 m3）
        Style = string.Equals(style?.Trim(), "miui", StringComparison.OrdinalIgnoreCase) ? "miui" : "m3";
        Apply(hex);
    }

    public static void Apply(string hex)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!Color.TryParse(hex, out var c)) return;

        // ---- 强调色（两种风格共用）----
        Set(app, "Accent",      c);
        Set(app, "AccentSoft",  Blend(c, Colors.White, 0.38));
        Set(app, "AccentText",  Blend(c, Colors.White, 0.58));
        Set(app, "AccentBg",    Blend(c, Color.Parse("#1E1A29"), 0.66));
        Set(app, "AccentHover", Blend(c, Color.Parse("#211D2D"), 0.52));
        Set(app, "AccentDeep",  Blend(c, Color.Parse("#15121D"), 0.42));
        Set(app, "AccentFill",  Color.FromArgb(48, c.R, c.G, c.B));
        Set(app, "StrokeStrong", Blend(c, Color.Parse("#3B3450"), 0.55));

        // ===== 黑白（mono）：纯中性灰，无任何色相 =====
        var isMono = c.R > 250 && c.G > 250 && c.B > 250;
        if (isMono)
        {
            Set(app, "Accent",      Color.Parse("#E9E9EA"));
            Set(app, "AccentSoft",  Color.Parse("#F2F2F3"));
            Set(app, "AccentText",  Color.Parse("#FFFFFF"));
            Set(app, "AccentBg",    Color.Parse("#2A2A2D"));
            Set(app, "AccentHover", Color.Parse("#333336"));
            Set(app, "AccentDeep",  Color.Parse("#3D3D41"));
            Set(app, "AccentFill",  Color.FromArgb(46, 255, 255, 255));
            Set(app, "StrokeStrong", Color.Parse("#55555C"));
            Set(app, "KnobFill",    Color.Parse("#141416"));     // 白色轨道上用深色滑块

            // 三层灰阶拉大：底 < 面板 < 卡片，靠亮度差表达层级
            Set(app, "Bg",         Color.Parse("#0B0B0D"));
            Set(app, "Panel",      Color.Parse("#121216"));
            Set(app, "PanelAlt",   Color.Parse("#1E1E24"));
            Set(app, "Stroke",     Color.Parse("#46464F"));
            Set(app, "StrokeSoft", Color.Parse("#2E2E36"));
            Set(app, "Hover",      Color.Parse("#2B2B32"));
            Set(app, "Pressed",    Color.Parse("#363640"));
            Set(app, "SelBg",      Color.Parse("#3F3F49"));
            Set(app, "CardSel",    Color.Parse("#4E4E59"));
            Set(app, "ChipBg",     Color.Parse("#26262C"));
            Set(app, "ChipBorder", Color.Parse("#44444D"));
            Set(app, "TrackBg",    Color.Parse("#3B3B44"));
            Set(app, "ChartBg",    Color.Parse("#101014"));

            app.Resources["RadiusCard"] = new CornerRadius(Style == "miui" ? 20 : 12);
            app.Resources["RadiusCtl"]  = new CornerRadius(Style == "miui" ? 12 : 6);
            app.Resources["RadiusPill"] = new CornerRadius(Style == "miui" ? 999 : 11);
            app.Resources["RowH"]       = Style == "miui" ? 38d : 34d;
            app.Resources["TreeRowH"]   = Style == "miui" ? 30d : 26d;

            // X / Grok 那种"白按钮 + 黑字"
            Set(app, "BtnPriBg",    Color.Parse("#F2F2F3"));
            Set(app, "BtnPriFg",    Color.Parse("#0A0A0B"));
            Set(app, "BtnPriHover", Color.Parse("#FFFFFF"));

            SetGhost(app, "SelGhost", "SelBg");
            SetSystemAccent(app, Color.Parse("#E9E9EA"));
            return;
        }

        Set(app, "KnobFill", Color.Parse("#FFFFFF"));

        if (Style == "miui")
        {
            // ===== HyperOS 风：中性面 + 大圆角 + 几乎无边框 =====
            Set(app, "Bg",         Color.Parse("#0A0A0C"));
            Set(app, "Panel",      Color.Parse("#121216"));
            Set(app, "PanelAlt",   Color.Parse("#202027"));   // 卡片明显比底色亮，靠亮度分层
            Set(app, "Stroke",     Color.Parse("#3A3A43"));
            Set(app, "StrokeSoft", Color.Parse("#2A2A32"));   // 内部分隔线很淡

            Set(app, "Hover",      Color.Parse("#2C2C34"));
            Set(app, "Pressed",    Color.Parse("#37373F"));
            Set(app, "SelBg",      Blend(c, Color.Parse("#34343E"), 0.74));
            Set(app, "CardSel",    Blend(c, Color.Parse("#42424C"), 0.58));
            Set(app, "ChipBg",     Color.Parse("#26262C"));
            Set(app, "ChipBorder", Color.Parse("#3A3A43"));
            Set(app, "TrackBg",    Color.Parse("#33333B"));
            Set(app, "ChartBg",    Color.Parse("#141418"));

            app.Resources["RadiusCard"] = new CornerRadius(20);
            app.Resources["RadiusCtl"]  = new CornerRadius(12);
            app.Resources["RadiusPill"] = new CornerRadius(999);
            app.Resources["RowH"]       = 38d;
            app.Resources["TreeRowH"]   = 30d;
        }
        else
        {
            // ===== Material You 风：表面由主题色轻染 =====
            Set(app, "Bg",         Blend(c, Color.Parse("#0C0B10"), 0.93));
            Set(app, "Panel",      Blend(c, Color.Parse("#121017"), 0.91));
            Set(app, "PanelAlt",   Blend(c, Color.Parse("#181520"), 0.885));
            Set(app, "Stroke",     Blend(c, Color.Parse("#2A2535"), 0.74));
            Set(app, "StrokeSoft", Blend(c, Color.Parse("#201C28"), 0.80));
            Set(app, "TrackBg",    Blend(c, Color.Parse("#241F2E"), 0.74));
            Set(app, "ChartBg",    Blend(c, Color.Parse("#131016"), 0.89));

            Set(app, "Hover",      Blend(c, Color.Parse("#221F2C"), 0.74));
            Set(app, "Pressed",    Blend(c, Color.Parse("#2A2638"), 0.58));
            Set(app, "SelBg",      Blend(c, Color.Parse("#2E2B3E"), 0.40));
            Set(app, "CardSel",    Blend(c, Color.Parse("#3D3460"), 0.36));
            Set(app, "ChipBg",     Blend(c, Color.Parse("#262135"), 0.70));
            Set(app, "ChipBorder", Blend(c, Color.Parse("#3D3654"), 0.56));

            app.Resources["RadiusCard"] = new CornerRadius(12);
            app.Resources["RadiusCtl"]  = new CornerRadius(6);
            app.Resources["RadiusPill"] = new CornerRadius(11);
            app.Resources["RowH"]       = 34d;
            app.Resources["TreeRowH"]   = 26d;
        }

        Set(app, "BtnPriBg",    Blend(c, Color.Parse("#1E1A29"), 0.66));
        Set(app, "BtnPriFg",    Blend(c, Colors.White, 0.58));
        Set(app, "BtnPriHover", Blend(c, Color.Parse("#211D2D"), 0.52));
        SetGhost(app, "SelGhost", "SelBg");
        SetSystemAccent(app, c);
    }

    /// <summary>让 Fluent 自带的控件（下拉、焦点框、选中）也用主题色，避免残留系统蓝</summary>
    private static void SetSystemAccent(Application app, Color c)
    {
        try
        {
            app.Resources["SystemAccentColor"] = c;
            app.Resources["SystemAccentColorLight1"] = Blend(c, Colors.White, 0.2);
            app.Resources["SystemAccentColorLight2"] = Blend(c, Colors.White, 0.35);
            app.Resources["SystemAccentColorLight3"] = Blend(c, Colors.White, 0.5);
            app.Resources["SystemAccentColorDark1"] = Blend(c, Colors.Black, 0.2);
            app.Resources["SystemAccentColorDark2"] = Blend(c, Colors.Black, 0.35);
            app.Resources["SystemAccentColorDark3"] = Blend(c, Colors.Black, 0.5);
        }
        catch { }
    }

    private static void Set(Application app, string key, Color c)
        => app.Resources[key] = new SolidColorBrush(c);

    /// <summary>
    /// 把某个 SolidColorBrush 复制成 alpha=0 的同色“幽灵”。
    /// 用途：选中底色的默认值。直接写 Transparent(#00FFFFFF) 时，
    /// BrushTransition 会从白色插值到紫色，中途闪白；同色幽灵才不会。
    /// </summary>
    private static void SetGhost(Application app, string ghostKey, string sourceKey)
    {
        if (app.Resources[sourceKey] is ISolidColorBrush b)
            app.Resources[ghostKey] = new SolidColorBrush(Color.FromArgb(0, b.Color.R, b.Color.G, b.Color.B));
    }

    private static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }
}
