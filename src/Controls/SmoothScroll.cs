using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DashFile.Controls;

/// <summary>
/// 鼠标滚轮平滑滚动（"跟手 + 缓出"），替代系统默认的"一格一跳"。
///
/// 为什么不用 Avalonia 自带的惯性：它只对触摸/拖拽生效，鼠标滚轮仍然是离散跳变。
///   这里拦截滚轮事件，把目标偏移累积起来，再用 150ms 的缓出动画把 Offset 推过去：
///   连续滚动时目标不断叠加，画面始终是连续运动，而不是一格一格闪。
///
/// 用法：XAML 上给任意控件加 <c>ctl:SmoothScroll.Enabled="True"</c>（会自动找它内部的 ScrollViewer），
/// 或者代码里 <see cref="Attach"/> 一个 ScrollViewer。
/// </summary>
public sealed class SmoothScroll
{
    private SmoothScroll() { }

    /// <summary>缓动时长：再长就跟不上手了</summary>
    private const double DurationMs = 150;

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<SmoothScroll, Control, bool>("Enabled");

    public static void SetEnabled(Control c, bool value) => c.SetValue(EnabledProperty, value);
    public static bool GetEnabled(Control c) => c.GetValue(EnabledProperty);

    private sealed class State
    {
        public ScrollViewer? Viewer;
        public double Start;
        public double Target;
        public readonly Stopwatch Watch = new();
        public DispatcherTimer? Timer;
    }

    private static readonly Dictionary<Control, State> States = new();

    static SmoothScroll()
    {
        EnabledProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            if (e.NewValue is true) Enable(c);
            else Disable(c);
        });
    }

    private static void Enable(Control control)
    {
        control.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    private static void Disable(Control control)
    {
        control.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
        if (States.Remove(control, out var state)) state.Timer?.Stop();
        control.DetachedFromVisualTree -= OnControlDetached;
    }

    private static void OnControlDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control) Disable(control);
    }

    /// <summary>直接挂到一个 ScrollViewer 上</summary>
    public static void Attach(ScrollViewer viewer) => SetEnabled(viewer, true);

    private static ScrollViewer? ViewerOf(Control control, State state)
    {
        if (state.Viewer != null) return state.Viewer;
        state.Viewer = control as ScrollViewer
                       ?? control.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        return state.Viewer;
    }

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not Control control) return;

        if (!States.TryGetValue(control, out var state))
        {
            state = new State();
            States[control] = state;
            // States 是静态强引用表：控件被虚拟化回收后必须解绑，否则漏控件 + 漏 15ms 定时器
            control.DetachedFromVisualTree += OnControlDetached;
        }

        var sv = ViewerOf(control, state);
        if (sv == null) return;

        var dy = e.Delta.Y;
        if (Math.Abs(dy) < 0.001) return;

        var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        if (max <= 0.5) return;   // 内容还没撑满：交给外层继续滚

        // 一格滚轮 ≈ 视口的 10%，至少 3 行；比系统默认的"一格 50px"更细腻
        var step = Math.Max(3 * 24, sv.Viewport.Height * 0.10);
        var delta = -dy * step;

        var from = state.Timer?.IsEnabled == true ? state.Target : sv.Offset.Y;
        var target = Math.Clamp(from + delta, 0, max);

        // 已经到顶/到底还往外滚：不拦截，允许滚动链把事件传给外层
        if (Math.Abs(target - sv.Offset.Y) < 0.5 && Math.Abs(delta) > 0.5) return;

        state.Start = sv.Offset.Y;
        state.Target = target;
        state.Watch.Restart();

        // 16ms ≈ 60Hz：和合成器同节拍，避免 15ms 定时器与帧边界互相追赶（追赶会表现为轻微抖动）
        state.Timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick(sv, state));
        state.Timer.Start();

        // 关键：拦掉系统默认的离散跳转，否则会先跳一下再被动画拉回来
        e.Handled = true;
    }

    private static void Tick(ScrollViewer sv, State state)
    {
        var t = Math.Clamp(state.Watch.Elapsed.TotalMilliseconds / DurationMs, 0, 1);

        // CubicEaseOut：起步快、收尾慢，滚轮的手感是"立刻动 + 柔和停"
        var eased = 1 - Math.Pow(1 - t, 3);
        var y = state.Start + (state.Target - state.Start) * eased;

        // 对齐到整像素：亚像素偏移会让每一帧都按新位置重新栅格化文字
        //   （实测就是"滚动时文字发虚 + 掉帧"）。整像素偏移可以直接复用已栅格化的字形。
        y = Math.Round(y);

        if (Math.Abs(y - sv.Offset.Y) >= 0.5)
            sv.Offset = new Vector(sv.Offset.X, y);

        if (t >= 1) state.Timer?.Stop();
    }
}
