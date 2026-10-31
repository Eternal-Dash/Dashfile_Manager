using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace DashFile.Controls;

/// <summary>
/// 选中时自动横向滚动长文本；滚到底停顿后回到开头循环。
/// 未选中时停在起点，不产生任何定时器开销。
/// </summary>
public class MarqueeText : TextBlock
{
    private readonly TranslateTransform _tt = new();
    private DispatcherTimer? _timer;
    private DispatcherTimer? _startDelay;
    private double _textWidth;
    private double _offset;
    private double _pause;
    private int _phase; // 0=滚动 1=停顿

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<MarqueeText, bool>(nameof(IsActive));

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public MarqueeText()
    {
        TextWrapping = TextWrapping.NoWrap;
        RenderTransform = _tt;
        ClipToBounds = false;
    }

    // 行被列表虚拟化回收后（Detach）16ms 定时器还在跑：以前没有这个兜底，
    //   滚动列表来回拖几次就堆一堆空转定时器。
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_timer is { IsEnabled: true }) _timer.Stop();
        ResetState();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimer();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty) UpdateTimer();
        else if (change.Property == TextBlock.TextProperty) ResetState();
    }

    private void ResetState()
    {
        _offset = 0;
        _pause = 0;
        _phase = 0;
        _tt.X = 0;
    }

    private void UpdateTimer()
    {
        if (IsActive)
        {
            // 悬停 220ms 后才开始滚：否则鼠标划过列表（尤其是滚动时行从指针下经过）
            //   会让每一行轮番启停 16ms 定时器 + 改 RenderTransform，直接把滚动帧率吃光。
            _startDelay ??= new DispatcherTimer(TimeSpan.FromMilliseconds(220), DispatcherPriority.Background,
                (_, _) =>
                {
                    _startDelay?.Stop();
                    if (!IsActive) return;
                    if (_textWidth <= Bounds.Width + 2) return;   // 放得下就不滚
                    _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, OnTick);
                    if (!_timer.IsEnabled) _timer.Start();
                });
            if (!_startDelay.IsEnabled) _startDelay.Start();
        }
        else
        {
            if (_startDelay != null) _startDelay.Stop();
            if (_timer != null) _timer.Stop();
            ResetState();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var natural = base.MeasureOverride(new Size(double.PositiveInfinity, availableSize.Height));
        _textWidth = natural.Width;
        var w = double.IsInfinity(availableSize.Width) ? natural.Width : availableSize.Width;
        return new Size(w, natural.Height);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var view = Bounds.Width;
        var overflow = _textWidth - view;
        if (overflow <= 2)
        {
            _tt.X = 0;
            return;
        }

        if (_phase == 1)
        {
            _pause -= 0.016;
            if (_pause <= 0) { _phase = 0; _offset = 0; _tt.X = 0; }
            return;
        }

        _offset -= 1.6;
        if (_offset <= -overflow)
        {
            _offset = -overflow;
            _tt.X = Math.Round(_offset);   // 整像素：亚像素位移会每帧重排文字
            _phase = 1;
            _pause = 1.3;
            return;
        }
        _tt.X = Math.Round(_offset);
    }
}
