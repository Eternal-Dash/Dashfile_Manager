using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;

namespace DashFile.Services;

/// <summary>
/// 极简 VectorDrawable 渲染器：解析二进制 XML 里的 &lt;vector&gt;/&lt;path&gt;，
/// 用 Skia 把 pathData 画出来。这样"只有矢量图标"的应用也能显示真图标。
/// </summary>
public static class VectorDrawableRenderer
{
    private sealed class El
    {
        public string Name = "";
        public Dictionary<string, uint> Refs = new();      // 资源引用型属性
        public Dictionary<string, string> Strs = new();    // 字符串型属性
        public Dictionary<string, float> Floats = new();   // 数值型属性
        public Dictionary<string, uint> Colors = new();    // 颜色型属性（0x1c~0x1f）
        public List<El> Children = new();
    }

    public static string Debug(byte[] axml)
    {
        var root = Parse(axml);
        if (root == null) return "root=null";
        var sb = new System.Text.StringBuilder();
        sb.Append($"root='{root.Name}' floats={string.Join(",", root.Floats.Keys)} colors={string.Join(",", root.Colors.Keys)} strs={string.Join(",", root.Strs.Keys)} children={root.Children.Count}");
        foreach (var c in root.Children)
            sb.Append($"\n  child='{c.Name}' floats={string.Join(",", c.Floats.Keys)} colors={string.Join(",", c.Colors.Keys)} strs={string.Join(",", c.Strs.Keys)} kids={c.Children.Count}");
        return sb.ToString();
    }

    /// <summary>是不是纯矢量图（root = vector）</summary>
    public static bool IsVector(byte[] axml)
    {
        var root = Parse(axml);
        return root != null && root.Name == "vector";
    }

    /// <summary>XML 根元素名</summary>
    public static string? RootName(byte[] axml) => Parse(axml)?.Name;

    /// <summary>调试：把 XML 结构打印出来</summary>
    public static string Dump(byte[] axml)
    {
        var root = Parse(axml);
        if (root == null) return "root=null";
        var sb = new System.Text.StringBuilder();
        void Walk(El e, int d)
        {
            var pad = new string(' ', d * 2);
            sb.Append($"{pad}<{e.Name}");
            foreach (var k in e.Refs.Keys) sb.Append($" ref:{k}=0x{e.Refs[k]:x8}");
            foreach (var k in e.Colors.Keys) sb.Append($" color:{k}=0x{e.Colors[k]:x8}");
            foreach (var k in e.Floats.Keys) sb.Append($" f:{k}={e.Floats[k]}");
            foreach (var k in e.Strs.Keys) sb.Append($" s:{k}={(e.Strs[k].Length > 40 ? e.Strs[k].Substring(0, 40) : e.Strs[k])}");
            sb.AppendLine(">");
            foreach (var c in e.Children) Walk(c, d + 1);
        }
        Walk(root, 0);
        return sb.ToString();
    }

    /// <summary>最近一次渲染失败原因（只读对外：可变 public 静态字段在多线程下没人能保证看到最新值）</summary>
    public static string LastError { get; private set; } = "";

    public static byte[]? RenderToPng(byte[] axml, int size, Func<uint, uint?> colorResolver)
    {
        try
        {
            var root = Parse(axml);
            if (root == null) return null;
            if (root.Name != "vector" && root.Name != "animated-vector" && root.Name != "bitmap") return null;
            if (root.Name == "bitmap") return null;

            var vw = root.Floats.TryGetValue("viewportWidth", out var a) ? a : 24f;
            var vh = root.Floats.TryGetValue("viewportHeight", out var b) ? b : 24f;
            if (vw <= 0 || vh <= 0) { vw = 24; vh = 24; }

            using var bitmap = new SKBitmap(size, size);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);

            var scale = Math.Min(size / vw, size / vh);
            canvas.Scale(scale);

            DrawChildren(canvas, root.Children, SKMatrix.Identity, colorResolver, vw, vh, 0);

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }
        catch (Exception ex) { LastError = ex.Message + " | " + ex.StackTrace?.Split('\n')[0]; return null; }
    }

    /// <summary>嵌套上限：畸形/恶意 vector drawable 可以嵌套几千层，无上限就是 StackOverflowException（进程直接消失）</summary>
    private const int MaxGroupDepth = 24;

    private static void DrawChildren(SKCanvas canvas, List<El> els, SKMatrix parentMatrix,
        Func<uint, uint?> colorResolver, float vw, float vh, int depth)
    {
        if (depth > MaxGroupDepth) return;   // 畸形嵌套：宁可少画，也不能让进程崩溃

        foreach (var el in els)
        {
            if (el.Name == "path")
            {
                DrawPath(canvas, el, parentMatrix, colorResolver);
            }
            else if (el.Name == "group")
            {
                var m = parentMatrix;
                var px = el.Floats.GetValueOrDefault("pivotX");
                var py = el.Floats.GetValueOrDefault("pivotY");
                var rot = el.Floats.GetValueOrDefault("rotation");
                var sx = el.Floats.TryGetValue("scaleX", out var s1) ? s1 : 1f;
                var sy = el.Floats.TryGetValue("scaleY", out var s2) ? s2 : 1f;
                var tx = el.Floats.GetValueOrDefault("translateX");
                var ty = el.Floats.GetValueOrDefault("translateY");

                m = SKMatrix.Concat(m, SKMatrix.CreateTranslation(tx, ty));
                if (rot != 0)
                {
                    m = SKMatrix.Concat(m, SKMatrix.CreateRotationDegrees(rot, px, py));
                }
                if (sx != 1 || sy != 1)
                {
                    var sm = SKMatrix.CreateScale(sx, sy);
                    var piv = SKMatrix.CreateTranslation(-px, -py);
                    var back = SKMatrix.CreateTranslation(px, py);
                    m = SKMatrix.Concat(m, SKMatrix.Concat(SKMatrix.Concat(piv, sm), back));
                }

                DrawChildren(canvas, el.Children, m, colorResolver, vw, vh, depth + 1);
            }
            else if (el.Name == "clip-path")
            {
                // 忽略裁剪，宁可多画也不要画错位置
            }
        }
    }

    private static void DrawPath(SKCanvas canvas, El el, SKMatrix matrix, Func<uint, uint?> colorResolver)
    {
        if (!el.Strs.TryGetValue("pathData", out var pathData) || string.IsNullOrWhiteSpace(pathData)) return;

        // SKPath 是原生对象：以前没人 Dispose，每画一次图标漏一个（CA2000）。
        //   这里原地收口，不用改 SvgPathParser 的对外签名。
        using var path = SvgPathParser.Parse(pathData);
        if (path == null) return;
        path.Transform(matrix);

        uint? fill = null;
        if (el.Colors.TryGetValue("fillColor", out var c1)) fill = c1;
        else if (el.Refs.TryGetValue("fillColor", out var r1)) fill = colorResolver(r1);

        if (fill.HasValue)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = ToSkColor(fill.Value)
            };
            canvas.DrawPath(path, paint);
        }

        uint? stroke = null;
        if (el.Colors.TryGetValue("strokeColor", out var c2)) stroke = c2;
        else if (el.Refs.TryGetValue("strokeColor", out var r2)) stroke = colorResolver(r2);

        if (stroke.HasValue)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                Color = ToSkColor(stroke.Value),
                StrokeWidth = el.Floats.GetValueOrDefault("strokeWidth", 1f)
            };
            canvas.DrawPath(path, paint);
        }
    }

    private static SKColor ToSkColor(uint argb)
    {
        var a = (byte)((argb >> 24) & 0xFF);
        var r = (byte)((argb >> 16) & 0xFF);
        var g = (byte)((argb >> 8) & 0xFF);
        var b = (byte)(argb & 0xFF);
        if (a == 0) a = 255;      // 部分矢量图 alpha 位是 0，按不透明处理
        return new SKColor(r, g, b, a);
    }

    // ---------------- 二进制 XML ----------------

    // ---------------- 二进制 XML ----------------

    /// <summary>
    /// 解析交给 <see cref="AxmlReader"/>（全项目唯一的 AXML 实现），这里只把元素属性
    /// 分到画图需要的四个桶里：引用 / 字符串 / 数值 / 颜色。
    /// </summary>
    private static El? Parse(byte[] b)
    {
        var root = AxmlReader.Parse(b);
        return root == null ? null : Convert(root);
    }

    private static El Convert(AxmlElement e)
    {
        var el = new El { Name = e.Name };
        foreach (var a in e.Attributes)
        {
            switch (a.DataType)
            {
                case 0x01: el.Refs[a.Name] = a.Data; break;                                  // 资源引用（@drawable/…）
                case 0x03: el.Strs[a.Name] = a.StringValue ?? ""; break;                     // 字符串
                case 0x04: el.Floats[a.Name] = BitConverter.Int32BitsToSingle(unchecked((int)a.Data)); break;  // float 位模式
                case 0x1c: case 0x1d: case 0x1e: case 0x1f: el.Colors[a.Name] = a.Data; break; // #AARRGGBB
                default: el.Strs[a.Name] = a.Data.ToString(CultureInfo.InvariantCulture); break;
            }
        }

        foreach (var c in e.Children) el.Children.Add(Convert(c));
        return el;
    }
}
