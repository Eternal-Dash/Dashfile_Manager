using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace DashFile.Views;

/// <summary>通用只读查看器：文本/十六进制报告、图片、字体预览。</summary>
public partial class FileViewerWindow : Window
{
    public FileViewerWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DashFile.Controls.ViewAnimator.AttachWindow(this);
        Opened += (_, _) => WindowPlacement.ClampToWorkingArea(this);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    public static async Task ShowTextAsync(Window owner, string title, string text)
    {
        var w = new FileViewerWindow { Title = title };
        w.FindControl<TextBlock>("PathText")!.Text = title;
        w.FindControl<TextBox>("Output")!.Text = text;
        await w.ShowDialog(owner);
    }

    public static async Task ShowImageAsync(Window owner, string title, byte[] bytes, bool ninePatch = false)
    {
        var w = new FileViewerWindow { Title = title };
        w.FindControl<TextBlock>("PathText")!.Text = title;
        w.FindControl<TextBox>("Output")!.IsVisible = false;
        w.FindControl<ScrollViewer>("ImageScroll")!.IsVisible = true;

        Bitmap bmp;
        try
        {
            // 解码是同步的，Bitmap 自己留一份像素，所以流可以立刻释放
            using var ms = new MemoryStream(bytes);
#pragma warning disable CA2000 // 位图交给窗口里的 Image 显示，所有权转移，这里不能 Dispose
            bmp = new Bitmap(ms);
#pragma warning restore CA2000
        }
        catch (Exception ex)
        {
            w.FindControl<TextBox>("Output")!.IsVisible = true;
            w.FindControl<TextBox>("Output")!.Text = "无法解码为图片：" + ex.Message;
            await w.ShowDialog(owner);
            return;
        }

        var canvas = w.FindControl<Canvas>("ImageCanvas")!;
        canvas.Width = bmp.PixelSize.Width;
        canvas.Height = bmp.PixelSize.Height;

        var img = new Image { Source = bmp, Stretch = Stretch.None, Width = bmp.PixelSize.Width, Height = bmp.PixelSize.Height };
        Canvas.SetLeft(img, 0); Canvas.SetTop(img, 0);
        canvas.Children.Add(img);

        if (ninePatch) AddNinePatchGuides(canvas, bytes, bmp.PixelSize.Width, bmp.PixelSize.Height);

        await w.ShowDialog(owner);
        bmp.Dispose();
    }

    /// <summary>点九预览：从 PNG 边框的黑色像素推导可拉伸区域，画参考线。</summary>
    private static void AddNinePatchGuides(Canvas canvas, byte[] png, int w, int h)
    {
        try
        {
            using var src = SKBitmap.Decode(png);
            if (src == null || src.Width < 3 || src.Height < 3) return;
            var brush = new SolidColorBrush(Color.Parse("#7FE3FF"));
            void VLine(int x) => canvas.Children.Add(new Avalonia.Controls.Shapes.Line
            {
                StartPoint = new Point(x + 0.5, 0), EndPoint = new Point(x + 0.5, h),
                Stroke = brush, StrokeThickness = 1,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 },
            });
            void HLine(int y) => canvas.Children.Add(new Avalonia.Controls.Shapes.Line
            {
                StartPoint = new Point(0, y + 0.5), EndPoint = new Point(w, y + 0.5),
                Stroke = brush, StrokeThickness = 1,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 },
            });

            for (var x = 1; x < w - 1; x++)
            {
                var c = src.GetPixel(x, 0);
                if (c.Red < 8 && c.Green < 8 && c.Blue < 8 && c.Alpha > 200) VLine(x);
            }
            for (var y = 1; y < h - 1; y++)
            {
                var c = src.GetPixel(0, y);
                if (c.Red < 8 && c.Green < 8 && c.Blue < 8 && c.Alpha > 200) HLine(y);
            }
        }
        catch { }
    }

    /// <summary>字体预览：用 SkiaSharp 渲染几种字号。</summary>
    public static async Task ShowFontAsync(Window owner, string title, byte[] fontBytes)
    {
        byte[] png;
        try { png = RenderFont(fontBytes); }
        catch (Exception ex)
        {
            await ShowTextAsync(owner, title, "无法解析字体：" + ex.Message);
            return;
        }
        await ShowImageAsync(owner, title, png);
    }

    private static byte[] RenderFont(byte[] fontBytes)
    {
        using var fontData = SKData.CreateCopy(fontBytes);
        using var typeface = SKTypeface.FromData(fontData)
                             ?? throw new InvalidOperationException("不是可识别的字体文件");
        const int width = 900, height = 560;
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(18, 18, 22));
        using var paint = new SKPaint { Color = new SKColor(232, 232, 238), IsAntialias = true, Typeface = typeface };
        var samples = new[] { "DashFile 字体预览", "abcdefg 0123456789", "中文样式：安卓文件管理器", "The quick brown fox jumps over the lazy dog" };
        var y = 70f;
        foreach (var size in new[] { 48f, 34f, 26f, 20f, 14f })
        {
            paint.TextSize = size;
            foreach (var s in samples)
            {
                canvas.DrawText(s, 32, y, paint);
                y += size + 14;
                if (y > height - 30) break;
            }
            y += 12;
            if (y > height - 30) break;
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
