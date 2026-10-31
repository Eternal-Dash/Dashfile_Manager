using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace DashFile.Controls;

/// <summary>
/// 统一的"线性过场"：淡入 + 轻微位移。
/// 监听 IsVisible 变 true 时播放，比较轻，不干扰操作。
///
/// 时长遵循 Material 的短/中时长（150~250ms）：再长就显得拖沓，
/// 再短又看不出是过渡。缓动统一用 Cubic/QuarticEaseOut（起步快、收尾稳）。
/// </summary>
public static class ViewAnimator
{
    public static void Attach(Control? c, double fromY = 10, int ms = 200, double fromX = 0)
    {
        if (c == null) return;

        c.PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.IsVisibleProperty) return;
            if (e.NewValue is not true) return;
            _ = PlayAsync(c, fromX, fromY, ms);
        };
    }

    /// <summary>
    /// 窗口内容入场：窗口本身不参与合成，所以动画做在 Content 上（对话框统一调用）。
    /// </summary>
    public static void AttachWindow(Window window, double fromY = 8, int ms = 180)
    {
        window.Opened += (_, _) =>
        {
            if (window.Content is Control content)
                _ = PlayAsync(content, 0, fromY, ms, fadeFrom: 0);
        };
    }

    private static async Task PlayAsync(Control c, double fromX, double fromY, int ms, double fadeFrom = 0.25)
    {
        try
        {
            if (c.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform(fromX, fromY);
                c.RenderTransform = tt;
            }

            var startX = fromX;
            var startY = fromY;

            // 先把起点摆好，避免上一次动画残留的位置造成"抖一下"
            tt.X = startX;
            tt.Y = startY;

            var anim = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(ms),
                Easing = new CubicEaseOut(),
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0d),
                        Setters =
                        {
                            new Setter(Visual.OpacityProperty, fadeFrom),
                            new Setter(TranslateTransform.XProperty, startX),
                            new Setter(TranslateTransform.YProperty, startY)
                        }
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1d),
                        Setters =
                        {
                            new Setter(Visual.OpacityProperty, 1d),
                            new Setter(TranslateTransform.XProperty, 0d),
                            new Setter(TranslateTransform.YProperty, 0d)
                        }
                    }
                }
            };

            await anim.RunAsync(c);
            c.Opacity = 1;
            tt.X = 0;
            tt.Y = 0;
        }
        catch { }
    }
}
