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
///   这里拦截整格滚轮事件，把目标偏移累积起来，再用 120ms 的缓出动画把 Offset 推过去：
///   连续滚动时目标不断叠加，画面始终是连续运动，而不是一格一格闪。
///
/// 用法：XAML 上给任意控件加 <c>ctl:SmoothScroll.Enabled="True"</c>（会自动找它内部的 ScrollViewer），
/// 或者代码里 <see cref="Attach"/> 一个 ScrollViewer。
/// </summary>
public sealed class SmoothScroll
{
    private SmoothScroll() { }

    /// <summary>缓动时长：再长就跟不上手了</summary>
    private const double DurationMs = 120;

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<SmoothScroll, Control, bool>("Enabled");

    public static void SetEnabled(Control c, bool value) => c.SetValue(EnabledProperty, value);
    public static bool GetEnabled(Control c) => c.GetValue(EnabledProperty);

    private sealed class State
    {
        public ScrollViewer? Viewer;
        public double Start;
        public double Target;
        public double LastWritten;
        public int Direction;
        public readonly Stopwatch Watch = new();
        public bool Active;
        public bool FramePending;
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
        control.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        control.DetachedFromVisualTree += OnControlDetached;
    }

    private static void Disable(Control control)
    {
        control.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
        control.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
        if (States.Remove(control, out var state)) state.Active = false;
        control.DetachedFromVisualTree -= OnControlDetached;
    }

    private static void OnControlDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // Keep the enabled handlers: the same control may be attached again without
        // changing its attached property (tab navigation / template reuse).
        if (sender is Control control && States.Remove(control, out var state)) state.Active = false;
    }

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && States.TryGetValue(control, out var state)) state.Active = false;
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
        }

        var sv = ViewerOf(control, state);
        if (sv == null) return;

        // Do not steal wheel input from an actual nested scroll viewer.
        var nested = (e.Source as Visual)?.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (nested != null && nested != sv && nested.Extent.Height > nested.Viewport.Height + 0.5) return;

        var dy = e.Delta.Y;
        if (Math.Abs(dy) < 0.001) return;

        // Fractional deltas already describe fine-grained movement. Leave those to
        // the platform rather than adding a second easing layer over a touchpad.
        if (Math.Abs(dy - Math.Round(dy)) > 0.001)
        {
            state.Active = false;
            return;
        }

        var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        if (max <= 0.5) return;   // 内容还没撑满：交给外层继续滚

        // 固定步长，不随窗口高度增长；反向时从当前位置计算，不追赶旧目标。
        const double step = 64;
        var delta = -dy * step;

        var direction = Math.Sign(delta);
        var continuing = state.Active && state.Direction == direction
                         && Math.Abs(sv.Offset.Y - state.LastWritten) < 1;
        var from = continuing ? state.Target : sv.Offset.Y;
        var target = Math.Clamp(from + delta, 0, max);

        // 已经到顶/到底还往外滚：不拦截，允许滚动链把事件传给外层
        if (Math.Abs(target - sv.Offset.Y) < 0.5 && Math.Abs(delta) > 0.5) return;

        state.Start = sv.Offset.Y;
        state.Target = target;
        state.LastWritten = sv.Offset.Y;
        state.Direction = direction;
        state.Watch.Restart();

        state.Active = true;
        RequestFrame(sv, state);

        // 关键：拦掉系统默认的离散跳转，否则会先跳一下再被动画拉回来
        e.Handled = true;
    }

    private static void RequestFrame(ScrollViewer sv, State state)
    {
        if (state.FramePending || !state.Active) return;
        var top = TopLevel.GetTopLevel(sv);
        if (top is null) { state.Active = false; return; }
        state.FramePending = true;
        top.RequestAnimationFrame(_ =>
        {
            state.FramePending = false;
            if (!state.Active) return;
            Tick(sv, state);
            RequestFrame(sv, state);
        });
    }

    private static void Tick(ScrollViewer sv, State state)
    {
        // Scrollbar dragging, keyboard scroll and file drag edge-scrolling own the
        // offset once they move it; a residual wheel animation must not pull back.
        if (!sv.IsEffectivelyVisible || Math.Abs(sv.Offset.Y - state.LastWritten) >= 1)
        {
            state.Active = false;
            return;
        }
        var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        state.Target = Math.Clamp(state.Target, 0, max);
        var t = Math.Clamp(state.Watch.Elapsed.TotalMilliseconds / DurationMs, 0, 1);

        // CubicEaseOut：起步快、收尾慢，滚轮的手感是"立刻动 + 柔和停"
        var eased = 1 - Math.Pow(1 - t, 3);
        var y = state.Start + (state.Target - state.Start) * eased;

        // 对齐到物理像素，避免高 DPI 下按逻辑整数取整造成不均匀步进。
        var scale = TopLevel.GetTopLevel(sv)?.RenderScaling ?? 1;
        y = Math.Clamp(Math.Round(y * scale) / scale, 0, max);

        if (Math.Abs(y - sv.Offset.Y) >= 0.5)
            sv.Offset = new Vector(sv.Offset.X, y);
        state.LastWritten = sv.Offset.Y;

        if (t >= 1) state.Active = false;
    }
}
