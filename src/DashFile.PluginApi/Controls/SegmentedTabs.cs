using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reflection;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace DashFile.PluginApi.Controls;

/// <summary>
/// 分段标签条：选中项由一个"滑动指示器"（segpill）表示，切换时宽度和水平位置一起缓动。
///
/// 两种用法（同一份实现，避免每个新功能各写一套定位/动画代码）：
///
///   1) 声明式 —— 直接在 XAML 里写按钮，适合固定标签（主窗口顶部那 6 个）：
///      &lt;Border Classes="seg"&gt;
///        &lt;ctl:SegmentedTabs SelectedIndex="{Binding SelectedTab}"&gt;
///          &lt;Button Classes="seg tabbtn" Content="设备信息"/&gt;
///          &lt;Button Classes="seg tabbtn" Content="文件管理"/&gt;
///        &lt;/ctl:SegmentedTabs&gt;
///      &lt;/Border&gt;
///
///   2) 数据驱动 —— 给 ItemsSource + DisplayMemberPath + SelectedItem，适合数量不定的标签
///      （插件页的"这个插件提供哪些子页面"就是这种）：
///      &lt;Border Classes="seg"&gt;
///        &lt;ctl:SegmentedTabs ItemsSource="{Binding PluginPages}" DisplayMemberPath="Title"
///                          SelectedItem="{Binding SelectedPluginPage, Mode=TwoWay}"/&gt;
///      &lt;/Border&gt;
///
/// 指示器是内建的第一个子元素（IsHitTestVisible=false，画在最底层），
/// 所以外面不要再叠自己的选中背景，否则会盖住滑动效果。
/// </summary>
public sealed class SegmentedTabs : Panel
{
    /// <summary>数据驱动的标签集合；为空时使用 XAML 里声明的按钮</summary>
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<SegmentedTabs, IEnumerable?>(nameof(ItemsSource));

    /// <summary>标签文字取自数据项的哪个属性（不填则用 ToString()）</summary>
    public static readonly StyledProperty<string?> DisplayMemberPathProperty =
        AvaloniaProperty.Register<SegmentedTabs, string?>(nameof(DisplayMemberPath));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<SegmentedTabs, object?>(nameof(SelectedItem),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<SegmentedTabs, int>(nameof(SelectedIndex), -1,
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>标签之间的间距（像素）</summary>
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SegmentedTabs, double>(nameof(Spacing), 2d);

    /// <summary>滑动动画时长；0 = 关闭动画（低配机 / 自动化测试可用）</summary>
    public static readonly StyledProperty<int> AnimationMsProperty =
        AvaloniaProperty.Register<SegmentedTabs, int>(nameof(AnimationMs), 260);

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string? DisplayMemberPath
    {
        get => GetValue(DisplayMemberPathProperty);
        set => SetValue(DisplayMemberPathProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public int AnimationMs
    {
        get => GetValue(AnimationMsProperty);
        set => SetValue(AnimationMsProperty, value);
    }

    /// <summary>选中项变化（含程序化设置 SelectedIndex）：宿主和插件都用它来切换页面</summary>
    public event EventHandler<int>? SelectionChanged;

    private void RaiseSelectionChanged()
    {
        try { SelectionChanged?.Invoke(this, SelectedIndex); } catch { }
    }

    // ---------------- 内部状态 ----------------

    private readonly Border _pill = new()
    {
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private readonly List<Button> _generated = new();
    private readonly HashSet<Button> _wired = new();
    private readonly Dictionary<(Type, string), PropertyInfo?> _propCache = new();

    private IList? _items;
    private IEnumerable? _itemsFor;
    private INotifyCollectionChanged? _itemsNotify;

    private bool _syncing;
    private bool _laidOut;
    private double _pillHeight;

    public SegmentedTabs()
    {
        _pill.Classes.Add("segpill");
        Children.Add(_pill);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        WireDeclarativeButtons();
        HookItems();
        UpdateClasses();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnhookItems();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsSourceProperty || change.Property == DisplayMemberPathProperty)
        {
            UnhookItems();
            HookItems();
            _items = null;
            _itemsFor = null;
            RebuildGenerated();
            UpdateClasses();
            InvalidateMeasure();
        }
        else if (change.Property == SelectedItemProperty && !_syncing)
        {
            var idx = IndexOfItem(SelectedItem);
            if (idx >= 0)
            {
                _syncing = true;
                try { SetCurrentValue(SelectedIndexProperty, idx); }
                finally { _syncing = false; }
            }
            UpdateClasses();
            PostPillUpdate();
            RaiseSelectionChanged();
        }
        else if (change.Property == SelectedIndexProperty && !_syncing)
        {
            var items = ItemList();
            if (items != null && SelectedIndex >= 0 && SelectedIndex < items.Count)
            {
                _syncing = true;
                try { SetCurrentValue(SelectedItemProperty, items[SelectedIndex]); }
                finally { _syncing = false; }
            }
            UpdateClasses();
            PostPillUpdate();
            RaiseSelectionChanged();
        }
        else if (change.Property == SpacingProperty)
        {
            InvalidateMeasure();
        }
        else if (change.Property == AnimationMsProperty)
        {
            ApplyPillTransitions();
        }
    }

    // ---------------- 布局 ----------------

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0d;
        var height = 0d;
        var visible = 0;

        foreach (var b in Buttons())
        {
            if (!b.IsVisible) continue;
            b.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            width += b.DesiredSize.Width;
            height = Math.Max(height, b.DesiredSize.Height);
            visible++;
        }

        if (visible > 1) width += Spacing * (visible - 1);
        _pillHeight = height;
        _pill.Measure(new Size(0, 0));
        return new Size(width, height);
    }

    /// <summary>上一次布局算出来的每个按钮矩形（索引 = 标签序号）</summary>
    private readonly List<Rect> _buttonRects = new();

    protected override Size ArrangeOverride(Size finalSize)
    {
        _buttonRects.Clear();
        var x = 0d;

        foreach (var b in Buttons())
        {
            if (!b.IsVisible)
            {
                _buttonRects.Add(default);
                continue;
            }
            var w = b.DesiredSize.Width;
            var h = Math.Max(b.DesiredSize.Height, finalSize.Height);
            b.Arrange(new Rect(x, 0, w, h));
            _buttonRects.Add(new Rect(x, 0, w, h));
            x += w + Spacing;
        }

        if (!_laidOut)
        {
            // 首帧直接就位（不带动画），之后才挂过渡 —— 否则窗口一打开指示器会从左边滑过来
            _laidOut = true;
            ApplyPillGeometry(animate: false);
            ApplyPillTransitions();
        }

        // 这里只"摆放"指示器，绝不改它的 Width / RenderTransform：
        //   在布局过程里改这两个属性不会走过渡，表现就是"一个消失、另一个突然出现"。
        //   位移与宽度一律走 ApplyPillGeometry（布局外，动画才生效）。
        var pw = _pill.Width;
        var ph = _pill.Height;
        _pill.Arrange(new Rect(0, 0,
            double.IsNaN(pw) || pw < 0 ? 0 : pw,
            double.IsNaN(ph) || ph < 0 ? 0 : ph));

        return finalSize;
    }

    /// <summary>把指示器移到当前选中项（animate=true 时由 Transitions 缓动过去）</summary>
    private void ApplyPillGeometry(bool animate)
    {
        var idx = SelectedIndex;
        if (idx < 0 || idx >= _buttonRects.Count || _buttonRects[idx].Width <= 0)
        {
            _pill.IsVisible = false;
            return;
        }

        var r = _buttonRects[idx];
        var h = Math.Max(r.Height, _pillHeight);

        // 不做动画时就地生效：临时摘掉过渡，写完再挂回去
        var keep = _pill.Transitions;
        if (!animate) _pill.Transitions = null;
        try
        {
            _pill.IsVisible = true;
            _pill.Width = r.Width;
            _pill.Height = h;
            // 用 TransformOperations 而不是 TranslateTransform：
            //   TransformOperationsTransition 只对 TransformOperations 做插值，
            //   换别的 ITransform 实现会直接跳到终点（就是"一个消失、一个出现"）。
            _pill.RenderTransform = TransformOperations.Parse($"translateX({r.X:0.##}px)");
        }
        finally
        {
            if (!animate) _pill.Transitions = keep;
        }

        InvalidateArrange();
    }

    /// <summary>
    /// 移动指示器。选中项变化时按钮尺寸不变，所以上一次布局的矩形立刻可用 ——
    /// 立即应用（这样 Transition 才会真正插值），再补一次布局后的校正。
    /// </summary>
    private void PostPillUpdate(bool animate = true)
    {
        if (!_laidOut) { InvalidateArrange(); return; }
        ApplyPillGeometry(animate);
        Dispatcher.UIThread.Post(() => ApplyPillGeometry(animate), DispatcherPriority.Loaded);
    }

    private void ApplyPillTransitions()
    {
        var ms = AnimationMs;
        if (ms <= 0 || !_laidOut) { _pill.Transitions = null; return; }

        var d = TimeSpan.FromMilliseconds(ms);
        _pill.Transitions = new Transitions
        {
            new TransformOperationsTransition
            {
                Property = RenderTransformProperty,
                Duration = d,
                Easing = new QuarticEaseOut(),
            },
            new DoubleTransition
            {
                Property = WidthProperty,
                Duration = d,
                Easing = new QuarticEaseOut(),
            },
        };
    }

    // ---------------- 标签维护 ----------------

    /// <summary>当前参与布局的按钮（声明式 + 生成式，按 Children 顺序）</summary>
    private List<Button> Buttons()
    {
        var list = new List<Button>(Children.Count);
        foreach (var c in Children)
        {
            if (ReferenceEquals(c, _pill)) continue;
            if (c is Button b) list.Add(b);
        }
        return list;
    }

    private void WireDeclarativeButtons()
    {
        foreach (var b in Buttons())
        {
            if (_generated.Contains(b)) continue;
            if (!_wired.Add(b)) continue;
            b.Click += OnDeclarativeClick;
        }
    }

    private void OnDeclarativeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var idx = Buttons().IndexOf(b);
        if (idx >= 0 && idx != SelectedIndex) SetCurrentValue(SelectedIndexProperty, idx);
    }

    private void RebuildGenerated()
    {
        foreach (var b in _generated)
        {
            b.Click -= OnGeneratedClick;
            Children.Remove(b);
        }
        _generated.Clear();

        var items = ItemList();
        if (items == null) return;

        for (var i = 0; i < items.Count; i++)
        {
            var b = new Button
            {
                Content = DisplayText(items[i]),
                Tag = items[i],
            };
            b.Classes.Add("seg");
            b.Classes.Add("tabbtn");
            b.Click += OnGeneratedClick;
            _generated.Add(b);
            Children.Add(b);
        }
    }

    private void OnGeneratedClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var items = ItemList();
        if (items == null) return;
        var idx = items.IndexOf(b.Tag);
        if (idx < 0) return;

        _syncing = true;
        try
        {
            SetCurrentValue(SelectedIndexProperty, idx);
            SetCurrentValue(SelectedItemProperty, items[idx]);
        }
        finally { _syncing = false; }

        UpdateClasses();
        PostPillUpdate();
        RaiseSelectionChanged();
    }

    private void UpdateClasses()
    {
        var buttons = Buttons();
        for (var i = 0; i < buttons.Count; i++)
        {
            var b = buttons[i];
            if (i == SelectedIndex)
            {
                if (!b.Classes.Contains("on")) b.Classes.Add("on");
            }
            else b.Classes.Remove("on");
        }
    }

    // ---------------- 数据源 ----------------

    private void HookItems()
    {
        _itemsNotify = ItemsSource as INotifyCollectionChanged;
        if (_itemsNotify != null) _itemsNotify.CollectionChanged += OnItemsChanged;
    }

    private void UnhookItems()
    {
        if (_itemsNotify != null) _itemsNotify.CollectionChanged -= OnItemsChanged;
        _itemsNotify = null;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 插件重载会整个换掉 PluginPages 的内容：重建标签并校正选中项
        _items = null;
        _itemsFor = null;
        RebuildGenerated();
        if (SelectedItem != null)
        {
            var idx = IndexOfItem(SelectedItem);
            if (idx >= 0)
            {
                _syncing = true;
                try { SetCurrentValue(SelectedIndexProperty, idx); }
                finally { _syncing = false; }
            }
        }
        UpdateClasses();
        InvalidateMeasure();
        PostPillUpdate(animate: false);
    }

    private IList? ItemList()
    {
        var src = ItemsSource;
        if (src == null) return null;
        if (ReferenceEquals(src, _itemsFor) && _items != null) return _items;

        _items = src as IList ?? new List<object?>(ToObjects(src));
        _itemsFor = src;
        return _items;
    }

    private static IEnumerable<object?> ToObjects(IEnumerable src)
    {
        foreach (var o in src) yield return o;
    }

    private int IndexOfItem(object? item)
    {
        var items = ItemList();
        if (items == null || item == null) return -1;
        return items.IndexOf(item);
    }

    private string DisplayText(object? item)
    {
        if (item == null) return "";
        var path = DisplayMemberPath;
        if (string.IsNullOrWhiteSpace(path)) return item.ToString() ?? "";

        var key = (item.GetType(), path!);
        if (!_propCache.TryGetValue(key, out var pi))
        {
            pi = item.GetType().GetProperty(path!, BindingFlags.Public | BindingFlags.Instance);
            _propCache[key] = pi;
        }
        return pi?.GetValue(item)?.ToString() ?? "";
    }
}