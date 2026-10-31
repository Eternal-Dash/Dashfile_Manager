using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using DashFile.Models;
using DashFile.Controls;
using DashFile.PluginApi;
using DashFile.Services;
using DashFile.Services.Plugins;
using DashFile.Services.OpenWith;
using DashFile.ViewModels;

namespace DashFile.Views;

public partial class MainWindow : Window
{
    // 拖出：按下后移动超过阈值即开始拖
    private bool _dragArmed;
    private Point _pressPos;
    private PosixEntry? _pressEntry;


    // 长按 / 多选
    private DispatcherTimer? _holdTimer;
    private PosixEntry? _holdEntry;
    private PosixEntry? _pendingToggle;
    private bool _multiMode;
    private ListBoxItem? _hlItem;

    // 内部拖拽数据格式（行 -> 同目录文件夹）
    private const string InternalFormat = "dashfile/paths";

    // 右键落点
    private Point _rightPos;
    private ContextMenu? _ctx;

    private MainViewModel? _subscribedVm;
    private PropertyChangedEventHandler? _vmPropertyHandler;

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedTab):
                // 标签指示器现在由 SegmentedTabs 自己维护，这里只管页面内的行高亮
                Dispatcher.UIThread.Post(() => UpdateRowPill(false), DispatcherPriority.Loaded);
                // 树/行的高亮是在隐藏状态下构建时量不出位置的（Bounds=0），
                //   切回文件页后必须重新量一次，否则"当前目录"那块高亮会消失。
                RequestTreePill();
                break;
            case nameof(MainViewModel.AppMode): UpdateModePill(); break;
            case nameof(MainViewModel.CurrentPath): AnimateFileList(); break;
        }
    }

    private void OnClosedUnsubscribe(object? sender, EventArgs e)
    {
        var vm = _subscribedVm;
        _subscribedVm = null;
        if (vm == null) return;
        try { vm.TreeVisualChanged -= RequestTreePill; } catch { }
        if (_vmPropertyHandler != null)
        {
            try { vm.PropertyChanged -= _vmPropertyHandler; } catch { }
        }
        Closed -= OnClosedUnsubscribe;
    }

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                // 两个订阅都要能解绑：以前是匿名 lambda，挂在 Vm 上一辈子（窗口关了也不放）
                _subscribedVm = vm;
                vm.TreeVisualChanged += RequestTreePill;
                _vmPropertyHandler ??= OnVmPropertyChanged;
                vm.PropertyChanged += _vmPropertyHandler;
                Closed += OnClosedUnsubscribe;

                MainViewModel.Perf("UI opened");
                await vm.InitializeAsync();
                MainViewModel.Perf("UI initialized");
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateModePill();
                    UpdateTreePill();
                }, DispatcherPriority.Loaded);
            }
        };
        SizeChanged += (_, _) =>
        {
            UpdateTreePill();
            Dispatcher.UIThread.Post(() => UpdateRowPill(false), DispatcherPriority.Loaded);
        };
        _filePage = this.FindControl<Control>("FilePage");


        // 页面/浮层过场动画
        ViewAnimator.Attach(this.FindControl<Control>("DevPage"), 10, 210);
        ViewAnimator.Attach(this.FindControl<Control>("FilePage"), 10, 210);
        ViewAnimator.Attach(this.FindControl<Control>("ModPage"), 10, 210);
        ViewAnimator.Attach(this.FindControl<Control>("PluginPage"), 10, 210);
        ViewAnimator.Attach(this.FindControl<Control>("SetPage"), 10, 210);
        ViewAnimator.Attach(this.FindControl<Control>("ToastHost"), 12, 170);
        ViewAnimator.AttachWindow(this, 6, 220);

        // 上次的窗口尺寸放到 Opened 里还原：那里才拿得到当前屏幕的工作区，
        // 才能判断记下来的值是不是"满屏/最大化"留下的，见 RestoreWindowSize()。

        Closing += OnClosingCleanup;

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        // 插件页拖入安装：窗口级先接，避免页面级 Drop 收不到
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnWindowDrop, RoutingStrategies.Tunnel);

        // 启动卡顿探针（DASHFILE_PERF=1）：UI 线程被同步阻塞 >150ms 就记一笔，
        //   否则"刚打开卡一阵"只能靠猜。
        if (AppEnvironment.Perf)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var probe = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            long last = -1;
            probe.Tick += (_, _) =>
            {
                var now = sw.ElapsedMilliseconds;
                if (last >= 0 && now - last > 150)
                    MainViewModel.Perf($"UI STALL {now - last}ms @{now}ms");
                last = now;
                if (now > 15000) probe.Stop();
            };
            probe.Start();
            MainViewModel.Perf("UI window-ctor");
        }
        Opened += (_, _) =>
        {
            RestoreWindowSize();      // 还原上次尺寸（满屏/最大化的值不复原）
            ClampToScreen();          // 换过显示器/分辨率后，保证窗口还在工作区内
            // 首次启动的引导页；勾过「以后不再显示」就不弹（见 WelcomeWindow.ShowOnceAsync）
            Dispatcher.UIThread.Post(() => { _ = WelcomeWindow.ShowOnceAsync(this); },
                                     DispatcherPriority.Background);
            UpdateSortGlyphs();
            HookDeviceNotifications();
            // 调试用：DASHFILE_DEBUG_DROPBAR=1 时直接显示"上传到当前目录"落点条
            if (string.Equals(Environment.GetEnvironmentVariable("DASHFILE_DEBUG_DROPBAR"), "1", StringComparison.Ordinal))
                ShowUploadBar(true);
        };

        var tree = this.FindControl<ItemsControl>("DirTree");
        tree?.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);

        Dispatcher.UIThread.Post(() =>
        {
            var sv = Files.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (sv != null)
                sv.ScrollChanged += (_, _) =>
                {
                    _hoverEntry = null;
                    SetHoverBorder(null);
                    // 滑块在滚动内容里，天然跟随；只有容器刚实现/还没定位时才补一次
                    if (!_rowPillShown) UpdateRowPill(false);
                };
        }, DispatcherPriority.Loaded);

        var files = Files;
        files.AddHandler(PointerPressedEvent, OnFilesPointerPressed, RoutingStrategies.Tunnel);
        files.AddHandler(PointerReleasedEvent, OnFilesPointerReleased, RoutingStrategies.Tunnel);
        files.AddHandler(PointerMovedEvent, OnFilesPointerMoved, RoutingStrategies.Tunnel);

        // 悬停在窗口层统一跟踪：列表项按下/选中后可能拿到指针捕获，
        //   只挂在 FilesList 上会漏事件，表现为“点完选中，高亮停在原地”。
        AddHandler(PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerExitedEvent, (_, _) => ClearFileHover(), RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, (_, _) => ClearFileHover(), RoutingStrategies.Tunnel);

        // 应用行 / 模块卡片：右键追加插件动作
        var apps = this.FindControl<ListBox>("AppsList");
        apps?.AddHandler(PointerPressedEvent, OnAppsPointerPressed, RoutingStrategies.Tunnel);
        var modScroll = this.FindControl<ScrollViewer>("ModScroll");
        modScroll?.AddHandler(PointerPressedEvent, OnModulesPointerPressed, RoutingStrategies.Tunnel);
    }


    // ================= 插件安装 / 卸载 =================

    private async void OnPluginInstallClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 .dfp 插件包",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("DashFile 插件包 (.dfp)") { Patterns = new[] { "*.dfp" } },
            },
        });
        if (files.Count == 0) return;
        await InstallDfpFilesAsync(files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!));
    }

    private async Task InstallDfpFilesAsync(IEnumerable<string> paths)
    {
        if (Vm == null) return;
        var list = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return;
        foreach (var path in list)
        {
            if (!string.Equals(Path.GetExtension(path), ".dfp", StringComparison.OrdinalIgnoreCase))
            {
                Vm.Toast("只接受 .dfp 插件包：" + Path.GetFileName(path));
                continue;
            }
            // 同一个 id 直接覆盖升级；管理器会先卸载旧版并保留回滚目录
            await Vm.InstallPluginPackageAsync(path, replaceExisting: true);
        }
    }

    private async void OnPluginUninstallClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedPlugin is not { } plugin) return;
        var choice = await ChoiceDialog.ShowAsync(this, "卸载插件",
            $"确定卸载「{plugin.Name}」？\n\n仅卸载会保留插件设置/缓存；卸载并删除数据会一并清理。",
            "仅卸载", "卸载并删除数据");
        if (choice == null) return;
        await Vm.UninstallPluginAsync(plugin, deleteData: choice == 1);
    }

    /// <summary>
    /// 从拖拽数据里取本机路径。Explorer 给的 DataObject 形态不止一种：
    /// 有时 GetFiles() 直接可用，有时要按 DataFormats.Files 取成 IStorageItem / string[]。
    /// 以前只用第一种，取不到就当成"不是文件"，表现就是拖进来毫无反应。
    /// </summary>
    private static List<string> DroppedLocalPaths(IDataObject data)
    {
        var list = new List<string>();

        void Add(IEnumerable<string?>? items)
        {
            if (items == null) return;
            foreach (var p in items)
                if (!string.IsNullOrWhiteSpace(p) && !list.Contains(p!, StringComparer.OrdinalIgnoreCase))
                    list.Add(p!);
        }

        try { Add(data.GetFiles()?.Select(i => i.TryGetLocalPath())); } catch { }

        if (list.Count == 0)
        {
            try
            {
                if (data.Get(DataFormats.Files) is IEnumerable<IStorageItem> items)
                    Add(items.Select(i => i.TryGetLocalPath()));
            }
            catch { }
        }

        if (list.Count == 0)
        {
            try
            {
                if (data.Get(DataFormats.Files) is IEnumerable<string> strs) Add(strs);
            }
            catch { }
        }

        return list;
    }

    /// <summary>
    /// 插件页拖入安装走窗口级隧道路由：页面级 Drop 在部分环境下收不到（光标变了但松手没反应），
    /// 这里在最外层先接一次，只处理"当前在插件页 + 拖的是 .dfp"。
    /// </summary>
    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        if (Vm?.IsPluginsTab != true) return;
        var paths = DroppedLocalPaths(e.Data);
        if (paths.Count == 0) return;
        var ok = paths.All(p => string.Equals(Path.GetExtension(p), ".dfp", StringComparison.OrdinalIgnoreCase));
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        PluginDropHint.IsVisible = ok;
        e.Handled = true;
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e)
    {
        if (Vm?.IsPluginsTab != true) return;
        var paths = DroppedLocalPaths(e.Data);
        if (paths.Count == 0)
        {
            // 有拖拽数据但取不到本机路径：记录下来，下次能直接看出是哪种形态
            AppLog.Add("PLUGIN DROP 无本机路径 files=" + e.Data.Contains(DataFormats.Files));
            Vm.Toast("没能读取拖入的文件路径，请改用「安装 .dfp」按钮");
            return;
        }

        e.Handled = true;
        PluginDropHint.IsVisible = false;
        AppLog.Add($"PLUGIN DROP {paths.Count} 项：{string.Join(",", paths.Select(Path.GetFileName))}");

        var dfp = paths.Where(p => string.Equals(Path.GetExtension(p), ".dfp", StringComparison.OrdinalIgnoreCase)).ToList();
        if (dfp.Count == 0)
        {
            Vm.Toast("只接受 .dfp 插件包");
            return;
        }
        await InstallDfpFilesAsync(dfp);
    }

    private void OnPluginDragOver(object? sender, DragEventArgs e)
    {
        var paths = DroppedLocalPaths(e.Data);
        if (paths.Count == 0)
        {
            e.DragEffects = DragDropEffects.None;
            PluginDropHint.IsVisible = false;
            return;
        }

        var ok = paths.All(p => string.Equals(Path.GetExtension(p), ".dfp", StringComparison.OrdinalIgnoreCase));
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        PluginDropHint.IsVisible = ok;
        e.Handled = true;
    }

    private void OnPluginDragLeave(object? sender, DragEventArgs e)
    {
        PluginDropHint.IsVisible = false;
    }

    private async void OnPluginDrop(object? sender, DragEventArgs e)
    {
        PluginDropHint.IsVisible = false;
        if (Vm == null) return;
        var paths = DroppedLocalPaths(e.Data);
        if (paths.Count == 0) return;
        e.Handled = true;
        await InstallDfpFilesAsync(paths);
    }

    // ================= 插件右键菜单 =================

    /// <summary>把插件注册的文件/应用/模块动作追加到现有右键菜单末尾。</summary>
    private void AppendPluginMenuItems(List<Control> items, PluginTargetKind kind, PluginTarget target)
    {
        if (Vm == null) return;
        var actions = Vm.GetPluginActions(kind);
        if (actions.Count == 0) return;

        if (items.Count > 0 && items[^1] is not Separator) items.Add(new Separator());

        var byPlugin = actions.GroupBy(a => a.Plugin.Id, StringComparer.OrdinalIgnoreCase).ToList();

        MenuItem ActionItem(PluginActionBinding binding)
        {
            var mi = new MenuItem
            {
                Header = binding.Action.Title,
                Icon = !string.IsNullOrWhiteSpace(binding.Action.Icon)
                    ? MenuIcons.CreatePath(binding.Action.Icon)
                    : MenuIcons.Create("plugin"),
                IsEnabled = CanRunPluginAction(binding, target),
            };
            if (!string.IsNullOrWhiteSpace(binding.Action.Description))
                Avalonia.Controls.ToolTip.SetTip(mi, binding.Action.Description);
            mi.Click += async (_, _) =>
            {
                if (Vm != null) await Vm.ExecutePluginActionAsync(binding, target);
            };
            return mi;
        }

        // 一律挂在"插件"子菜单下：鼠标悬停展开，插件加的功能都收在这里，不往主菜单里散。
        var root = MenuIcons.Item("插件", "plugin");
        var children = new List<Control>();
        if (byPlugin.Count == 1)
        {
            foreach (var binding in byPlugin[0]) children.Add(ActionItem(binding));
        }
        else
        {
            foreach (var group in byPlugin)
            {
                var pluginMenu = MenuIcons.Item(group.First().Plugin.Name, "plugin");
                var actionsInPlugin = new List<Control>();
                foreach (var binding in group) actionsInPlugin.Add(ActionItem(binding));
                pluginMenu.ItemsSource = actionsInPlugin;
                children.Add(pluginMenu);
            }
        }
        root.ItemsSource = children;
        items.Add(root);
    }

    private bool CanRunPluginAction(PluginActionBinding binding, PluginTarget target)
    {
        try { return binding.Action.CanExecute?.Invoke(target) ?? true; }
        catch { return false; }
    }

    private void OnAppsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var list = this.FindControl<ListBox>("AppsList");
        if (list == null || Vm == null) return;
        var pt = e.GetCurrentPoint(list);
        if (!pt.Properties.IsRightButtonPressed) return;

        var item = (e.Source as Control)?.DataContext as AppItem;
        if (item == null) return;
        if (Vm.GetPluginActions(PluginTargetKind.App).Count == 0) return;

        list.SelectedItem = item;
        var items = new List<Control>();
        AppendPluginMenuItems(items, PluginTargetKind.App,
            PluginTarget.ForApp(item.Package, item.Label, item.ApkPath));
        while (items.Count > 0 && items[^1] is Separator) items.RemoveAt(items.Count - 1);
        if (items.Count == 0) return;

        var menu = MenuFor(list);
        menu.ItemsSource = items;
        menu.Open(list);
        e.Handled = true;
    }

    private void OnModulesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm == null) return;
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsRightButtonPressed) return;

        var item = (e.Source as Control)?.DataContext as ModuleInfo;
        if (item == null) return;
        if (Vm.GetPluginActions(PluginTargetKind.Module).Count == 0) return;

        var items = new List<Control>();
        AppendPluginMenuItems(items, PluginTargetKind.Module,
            PluginTarget.ForModule(item.Id, item.Name, item.Dir));
        while (items.Count > 0 && items[^1] is Separator) items.RemoveAt(items.Count - 1);
        if (items.Count == 0) return;

        var target = sender as Control ?? this;
        var menu = MenuFor(target);
        menu.ItemsSource = items;
        menu.Open(target);
        e.Handled = true;
    }

    /// <summary>
    /// 复用同一个 ContextMenu 实例：以前每次右键都 new 一个，连同十几个 MenuItem 和它们的闭包
    /// 一起挂着（菜单关闭后 ItemsSource 还握着整批控件），长时间用会一路涨句柄。
    /// 这里关旧菜单、清空 ItemsSource，再交给调用方塞新的一批。
    /// </summary>
    private ContextMenu MenuFor(Control target)
    {
        if (_ctx == null) _ctx = new ContextMenu();
        else
        {
            try { _ctx.Close(); } catch { }
            _ctx.ItemsSource = null;     // 放开上一批 MenuItem（连同其 Click 闭包）
        }
        _ctx.Placement = PlacementMode.Pointer;
        _ctx.PlacementTarget = target;
        return _ctx;
    }

    /// <summary>
    /// 还原上次关闭时的窗口尺寸，但只在"普通窗口"的范围内生效：
    /// 上次是最大化、或者尺寸贴满工作区（≥ 92%）时不还原，回落到 XAML 里的默认尺寸。
    /// 以前是把 Bounds 原样存下再原样套用，于是最大化过一次之后，之后每次启动都是满屏、再也回不去。
    /// </summary>
    private void RestoreWindowSize()
    {
        try
        {
            var st = AppSettings.Current;
            var w = st.WindowWidth;
            var h = st.WindowHeight;
            if (w < MinWidth || h < MinHeight) return;   // 没存过，或值不可信

            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen != null)
            {
                var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
                var waW = screen.WorkingArea.Width / scale;
                var waH = screen.WorkingArea.Height / scale;
                if (w >= waW * 0.92 || h >= waH * 0.92) return;   // 满屏/最大化的残留值，不继承
                Width = Math.Min(w, waW);
                Height = Math.Min(h, waH);
            }
            else
            {
                Width = w;
                Height = h;
            }
        }
        catch { }
    }

    /// <summary>把窗口尺寸钳制到屏幕工作区内，并居中（避免超出屏幕被裁）</summary>
    private void ClampToScreen()
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen == null) return;
            var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            var waW = screen.WorkingArea.Width / scale;
            var waH = screen.WorkingArea.Height / scale;
            Width = Math.Min(Width, waW - 8);
            Height = Math.Min(Height, waH - 8);
            Position = new PixelPoint(
                screen.WorkingArea.X + (int)((screen.WorkingArea.Width - Width * scale) / 2),
                screen.WorkingArea.Y + (int)((screen.WorkingArea.Height - Height * scale) / 2));
        }
        catch { }
    }

    private MainViewModel? Vm => DataContext as MainViewModel;
    private ListBox Files => this.FindControl<ListBox>("FilesList")!;
    private ItemsControl? TreeBox => this.FindControl<ItemsControl>("DirTree");

    private PosixEntry? Sel => Vm?.SelectedEntry;
    private List<PosixEntry> SelectedEntries()
        => Files.SelectedItems?.OfType<PosixEntry>().ToList() ?? new List<PosixEntry>();

    // ================= 导航 =================

    private void OnCrumbClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        if (sender is Button b && b.Tag is string path && !string.IsNullOrEmpty(path))
            _ = Vm.NavigateAsync(path);
    }

    private void OnPathCommit(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        _ = Vm.GoAsync();
        Vm.IsPathEditing = false;
    }

    private void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm != null) { _ = Vm.GoAsync(); Vm.IsPathEditing = false; e.Handled = true; }
    }

    private void OnHeaderSort(object? sender, RoutedEventArgs e)
    {
        if (Vm == null || sender is not Button b || b.Tag is not string col) return;
        Vm.SortBy(col);
        UpdateSortGlyphs();
    }

    private void UpdateSortGlyphs()
    {
        if (Vm == null) return;
        void Mark(Button? btn, string col, string label)
        {
            if (btn == null) return;
            btn.Content = Vm.SortColumn == col ? label + (Vm.SortAsc ? " ▲" : " ▼") : label;
            btn.Foreground = Vm.SortColumn == col
                ? (IBrush?)Application.Current?.Resources["TextMain"]
                : (IBrush?)Application.Current?.Resources["TextMuted"];
        }
        Mark(this.FindControl<Button>("HdrName"), "name", "名称");
        Mark(this.FindControl<Button>("HdrDate"), "date", "修改日期");
        Mark(this.FindControl<Button>("HdrType"), "type", "类型");
        Mark(this.FindControl<Button>("HdrSize"), "size", "大小");
    }

    // ================= 快捷键 =================

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm == null) return;
        var typing = e.Source is TextBox;

        // 像资源管理器一样用方向键/Home/End/PageUp/PageDown 移动选择，Shift 连选，Enter 打开
        if (!typing && Vm.IsFileTab)
        {
            switch (e.Key)
            {
                case Key.Up:
                case Key.Down:
                case Key.Home:
                case Key.End:
                case Key.PageUp:
                case Key.PageDown when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    MoveFileSelection(e.Key, false);
                    e.Handled = true;
                    return;
                case Key.Enter:
                    if (Sel != null)
                    {
                        _ = OpenEntrySmartAsync(Sel);
                        e.Handled = true;
                    }
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.F5:
                _ = Vm.RefreshAsync();
                e.Handled = true;
                return;

            case Key.Back when !typing && !Vm.IsArchiveMode:
                _ = Vm.GoUpAsync();
                e.Handled = true;
                return;

            case Key.Delete when !typing:
                if (Files.SelectedItems?.Count > 0) OnDeleteClick(null!, null!);
                e.Handled = true;
                return;

            case Key.F2 when !typing:
                if (Sel != null) OnRenameClick(null!, null!);
                e.Handled = true;
                return;

            case Key.A when !typing && e.KeyModifiers.HasFlag(KeyModifiers.Control):
                Files.SelectAll();
                e.Handled = true;
                return;

            case Key.Escape:
                if (Vm.IsMultiSelect) ExitMultiSelect();
                else if (Vm.IsArchiveMode) { /* 留给用户点按钮 */ }
                else Files.SelectedItems?.Clear();
                e.Handled = true;
                return;
        }
    }

    private int _selCursor = -1;
    private int _selAnchor = -1;

    /// <summary>方向键移动文件选择（Shift = 连选），并滚动到可见区域。</summary>
    private void MoveFileSelection(Key key, bool extend)
    {
        if (Vm == null) return;
        var items = Vm.Entries;
        if (items == null || items.Count == 0) return;

        if (_selCursor < 0 || _selCursor >= items.Count)
        {
            var cur = Files.SelectedItem as PosixEntry;
            _selCursor = cur == null ? -1 : items.IndexOf(cur);
        }

        int idx;
        if (key == Key.Home) idx = 0;
        else if (key == Key.End) idx = items.Count - 1;
        else
        {
            var step = key switch
            {
                Key.Up => -1,
                Key.Down => 1,
                Key.PageUp => -12,
                Key.PageDown => 12,
                _ => 0,
            };
            idx = _selCursor < 0 ? (step >= 0 ? 0 : items.Count - 1) : _selCursor + step;
        }
        idx = Math.Clamp(idx, 0, items.Count - 1);
        var target = items[idx];

        if (extend)
        {
            if (_selAnchor < 0) _selAnchor = _selCursor < 0 ? idx : _selCursor;
            Files.SelectedItems?.Clear();
            var a = Math.Min(_selAnchor, idx);
            var b = Math.Max(_selAnchor, idx);
            for (var i = a; i <= b; i++) Files.SelectedItems?.Add(items[i]);
        }
        else
        {
            Files.SelectedItems?.Clear();
            Files.SelectedItem = target;
            _selAnchor = idx;
        }

        _selCursor = idx;
        try { Files.ScrollIntoView(target); } catch { }
        Files.Focus();
    }

    // ================= 关闭时清理 =================

    /// <summary>true = 已经清理完，允许真正关闭</summary>
    private bool _cleanupDone;

    /// <summary>true = 清理正在进行（防止并发跑第二份 CleanupAsync）</summary>
    private bool _cleanupRunning;

    private async void OnClosingCleanup(object? sender, WindowClosingEventArgs e)
    {
        // 判断顺序是这里唯一重要的事：
        //   ① 已经清理完 → 直接放行（下面 finally 里那次 Close() 走的正是这条路）
        //   ② 正在清理   → 拦下，别跑第二份 CleanupAsync
        // 以前只有一个 _closing 标志、而且从不清零，于是清理结束后自己调的 Close() 又被自己按了
        // e.Cancel=true —— 窗口永远关不掉，界面还停在 IsEnabled=false 的遮罩上，
        // 用户看到的就是"无限期清理文件"。
        if (_cleanupDone) return;

        // 保存窗口尺寸：只在"普通窗口"状态、且没贴满工作区时保存。
        // 否则最大化的 Bounds 会被存成默认尺寸，下次启动直接铺满屏幕。
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            var scale = screen == null || screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            var waW = screen == null ? double.MaxValue : screen.WorkingArea.Width / scale;
            var waH = screen == null ? double.MaxValue : screen.WorkingArea.Height / scale;
            var w = Bounds.Width;
            var h = Bounds.Height;
            var normal = WindowState == WindowState.Normal
                         && w >= MinWidth && h >= MinHeight
                         && w < waW * 0.92 && h < waH * 0.92;
            if (normal)
            {
                var st = AppSettings.Current;
                st.WindowWidth = w;
                st.WindowHeight = h;
                AppSettings.Save();
            }
        }
        catch { }

        if (_cleanupRunning) { e.Cancel = true; return; }
        _cleanupRunning = true;

        e.Cancel = true;                       // 先拦下关闭
        var overlay = this.FindControl<Border>("CleanupOverlay");
        if (overlay != null) overlay.IsVisible = true;
        try { IsEnabled = false; } catch { }

        try
        {
            // 清理通常几十毫秒就完成，配一个最短展示时间，让提示能被看见
            var cleanup = Vm?.CleanupAsync() ?? Task.CompletedTask;
            var minShow = Task.Delay(700);
            // 清理只是"顺手保存状态"，不能当成关窗口的前置条件：
            //   以前无条件 await，清理一挂住窗口就永远关不掉，而界面已经 IsEnabled=false，用户没退路。
            var all = Task.WhenAll(cleanup, minShow);
            var done = await Task.WhenAny(all, Task.Delay(6000));
            if (done != all) AppLog.Add("关闭清理超时（6s），已强制退出");
        }
        catch { }
        finally
        {
            try { AppService.FlushLabels(); } catch { }
            UnhookDeviceNotifications();

            _cleanupDone = true;               // 必须先置位，否则下面这个 Close() 会再次被自己拦住
            _cleanupRunning = false;

            // 从 Closing 事件里同步再进一次 Close() 容易踩重入，Post 出去更稳
            Dispatcher.UIThread.Post(() =>
            {
                Close();

                // 兜底：万一还有别的东西在拦关闭，2.5 秒后强制退出。
                // 宁可丢一点收尾工作，也不能让用户卡在一个关不掉的窗口上。
                DispatcherTimer.RunOnce(() =>
                {
                    if (IsVisible)
                    {
                        AppLog.Add("窗口未能关闭（仍有 Cancel），已强制退出");
                        Environment.Exit(0);
                    }
                }, TimeSpan.FromMilliseconds(2500));
            });
        }
    }

    // ================= 目录树 =================

    private TreeRow? TreeRowAt(Point p)
    {
        var tree = TreeBox;
        if (tree == null) return null;
        var c = tree.InputHitTest(p) as Control;
        while (c != null)
        {
            if (c.DataContext is TreeRow row) return row;
            c = c.Parent as Control;
        }
        return null;
    }

    private void OnTreeGlyphClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        if (sender is Control c && c.DataContext is TreeRow row)
        {
            if (row.Node != null) _ = Vm.ToggleNodeAsync(row.Node);
            else _ = Vm.ToggleNodeByPathAsync(row.FullPath);   // 置顶行：Node 可能还没建立
        }
        e.Handled = true;
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm == null) return;
        var tree = TreeBox;
        if (tree == null) return;
        var pt = e.GetCurrentPoint(tree);

        // 点在展开箭头上：交给按钮处理，不导航
        if (IsTreeGlyph(e.Source)) return;

        var row = TreeRowAt(pt.Position);
        if (row == null) return;

        if (pt.Properties.IsRightButtonPressed)
        {
            ShowFolderMenu(row);
            e.Handled = true;
            return;
        }

        if (pt.Properties.IsLeftButtonPressed &&
            !string.Equals(row.FullPath.TrimEnd('/'), Vm.CurrentPath.TrimEnd('/'), StringComparison.Ordinal))
        {
            Vm.PreviewTreeCurrent(row.FullPath);   // 先把高亮滑过去，别等 ls
            _ = Vm.NavigateAsync(row.FullPath);
        }
    }

    private static bool IsTreeGlyph(object? src)
    {
        var c = src as Control;
        while (c != null)
        {
            if (c is Button b && b.Classes.Contains("treeexp")) return true;
            c = c.Parent as Control;
        }
        return false;
    }

    private void ShowFolderMenu(TreeRow row)
    {
        if (Vm == null) return;

        var items = new List<Control>();
        void Add(string header, Action act)
        {
            var mi = MenuIcons.Item(header);
            mi.Click += (_, __) => act();
            items.Add(mi);
        }
        void Sep() { if (items.Count > 0 && items[^1] is not Separator) items.Add(new Separator()); }

        Add("打开此目录", () => _ = Vm.NavigateAsync(row.FullPath));
        Sep();
        Add("在此新建文件…", async () =>
        {
            var name = await InputDialog.ShowAsync(this, "新建文件", $"在 {row.Name} 中新建文件", "新文件.txt");
            if (!string.IsNullOrWhiteSpace(name)) await Vm.NewFileInAsync(row.FullPath, name!.Trim());
        });
        Add("在此新建文件夹…", async () =>
        {
            var name = await InputDialog.ShowAsync(this, "新建文件夹", $"在 {row.Name} 中新建文件夹", "新建文件夹");
            if (!string.IsNullOrWhiteSpace(name)) await Vm.NewFolderInAsync(row.FullPath, name!.Trim());
        });
        Add("上传文件到此目录…", () => _ = UploadToAsync(row.FullPath));
        Sep();
        Add("重命名…", async () =>
        {
            var name = await InputDialog.ShowAsync(this, "重命名", "新名称", row.Name);
            if (!string.IsNullOrWhiteSpace(name) && name!.Trim() != row.Name)
                await Vm.RenameNodeAsync(row.FullPath, name.Trim());
        });
        Add("删除", async () =>
        {
            var ok = await ConfirmDialog.ShowAsync(this, "删除文件夹",
                $"确定删除「{row.Name}」及其全部内容吗？\n此操作不可撤销。");
            if (ok) await Vm.DeleteNodeAsync(row.FullPath);
        });
        Sep();
        Add("刷新目录树", () => _ = Vm.RefreshTreeAsync());
        AppendPluginMenuItems(items, PluginTargetKind.Directory,
            PluginTarget.ForDirectory(row.FullPath, row.Name));

        while (items.Count > 0 && items[^1] is Separator) items.RemoveAt(items.Count - 1);
        var menu = MenuFor(TreeBox!);
        menu.ItemsSource = items;
        menu.Open(TreeBox!);
    }

    // ---------- 拖到目录树 ----------

    private void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        var tree = TreeBox;
        if (tree == null) return;
        var row = TreeRowAt(e.GetPosition(tree));
        var ok = row != null &&
                 (e.Data.Contains(InternalFormat) || e.Data.Contains(DataFormats.Files));
        e.DragEffects = ok ? (e.Data.Contains(InternalFormat) ? DragDropEffects.Move : DragDropEffects.Copy)
                           : DragDropEffects.None;
        SetTreeHighlight(ok ? row : null);
        if (Vm != null)
            Vm.DragHint = ok ? $"松开即放到「{row!.Name}」" : "拖到目录树里的文件夹上";
        e.Handled = true;
    }

    private TreeRow? _treeDropRow;

    /// <summary>拖拽落点：目录树行高亮（拖进来/拖进去都用这一条）</summary>
    private void SetTreeHighlight(TreeRow? row)
    {
        if (ReferenceEquals(_treeDropRow, row)) return;
        if (_treeDropRow != null) _treeDropRow.IsDropTarget = false;
        _treeDropRow = row;
        if (row != null) row.IsDropTarget = true;
    }

    private async void OnTreeDrop(object? sender, DragEventArgs e)
    {
        SetTreeHighlight(null);
        if (Vm == null) return;
        var tree = TreeBox;
        if (tree == null) return;
        var row = TreeRowAt(e.GetPosition(tree));
        if (row == null) return;

        if (e.Data.Contains(InternalFormat))
        {
            // e.Handled 必须在第一个 await 之前置位：await 之后路由已经继续传给上层，
            //   同一批文件会被上层再处理一次（移动完又被上传一份）。
            e.Handled = true;
            var paths = e.Data.Get(InternalFormat) as string[];
            if (paths is { Length: > 0 }) await Vm.MoveAsync(paths, row.FullPath);
            return;
        }

        var files = e.Data.GetFiles();
        if (files == null) return;
        var locals = files.Select(x => x.TryGetLocalPath())
                          .Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToList();
        if (locals.Count > 0) await Vm.UploadToAsync(locals, row.FullPath);
        e.Handled = true;
    }

    private void OnFilesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        Vm?.UpdateSelectionInfo(SelectedEntries());
        if (_multiMode) UpdateMultiHint();
        // 新行的 Bounds 要等一帧才准
        Dispatcher.UIThread.Post(() => UpdateRowPill(), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 选中滑块：Canvas 在 ScrollViewer 的内容里，滚动时天然跟随；
    /// 换行时只改 translateY，由 XAML 的过渡负责滑动。
    /// </summary>
    private void UpdateRowPill(bool animate = true)
    {
        // 名字在 ControlTemplate 的独立 namescope 里，Window.FindControl 找不到，要从列表的视觉树里取
        var pill = this.FindControl<Border>("RowPill")
                   ?? Files.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "RowPill");
        var layer = this.FindControl<Canvas>("RowPillLayer")
                    ?? Files.GetVisualDescendants().OfType<Canvas>().FirstOrDefault(c => c.Name == "RowPillLayer");
        if (pill == null || layer == null)
        {
            if (MenuDebug) AppLog.Add($"PILL missing pill={pill != null} layer={layer != null}");
            return;
        }

        var sel = Files.SelectedItems?.OfType<PosixEntry>().ToList() ?? new List<PosixEntry>();
        var multi = sel.Count > 1;
        Files.Classes.Set("multi", multi);

        var shown = false;
        if (!multi && sel.Count == 1 &&
            Files.ContainerFromItem(sel[0]) is ListBoxItem li && li.Bounds.Height > 0)
        {
            var pos = li.TranslatePoint(new Point(0, 0), layer);
            if (pos != null)
            {
                pill.Width = Math.Max(24, li.Bounds.Width - 4);
                pill.Height = li.Bounds.Height;
                Canvas.SetLeft(pill, 2);
                var target = Avalonia.Media.Transformation.TransformOperations.Parse($"translateY({pos.Value.Y:0.##}px)");
                if (!_rowPillShown || !animate)
                {
                    var saved = pill.Transitions;
                    pill.Transitions = null;
                    pill.RenderTransform = target;
                    if (saved != null)
                        Dispatcher.UIThread.Post(() => pill.Transitions = saved, DispatcherPriority.Render);
                }
                else
                {
                    pill.RenderTransform = target;
                }
                shown = true;
            }
        }

        pill.IsVisible = shown;
        _rowPillShown = shown;
        Files.Classes.Set("nopill", !shown && !multi && sel.Count == 1);
        if (MenuDebug)
            AppLog.Add($"PILL sel={sel.Count} multi={multi} shown={shown} " +
                       $"layer={(layer.Bounds.Width):0}x{(layer.Bounds.Height):0}");
    }

    private void HideRowPill()
    {
        _rowPillShown = false;
        var pill = this.FindControl<Border>("RowPill")
                   ?? Files.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "RowPill");
        if (pill != null) pill.IsVisible = false;
        Files.Classes.Set("nopill", false);
    }

    /// <summary>
    /// 目录树的"当前项"高亮：从旧节点滑到新节点。
    /// Canvas 放在滚动内容里，所以天然跟随滚动，不做额外偏移计算。
    /// </summary>
    private bool _treePillQueued;

    /// <summary>
    /// 合并同一帧内的多次高亮重算：树每次重排都会触发 TreeVisualChanged，
    /// 直接量位置会强迫布局，快速导航/启动时会叠加成卡顿。
    /// </summary>
    private void RequestTreePill()
    {
        if (_treePillQueued) return;
        _treePillQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _treePillQueued = false;
            UpdateTreePill();
        }, DispatcherPriority.Background);
    }

    private void UpdateTreePill()
    {
        // 文件页没显示时量不到行（Bounds=0），等切页时再算，省一次全树布局
        if (this.FindControl<Control>("FilePage") is { IsVisible: false }) return;
        var pill = this.FindControl<Border>("TreePill");
        var overlay = this.FindControl<Canvas>("TreeOverlay");
        var treeBox = this.FindControl<ItemsControl>("DirTree");
        var host = this.FindControl<Grid>("TreeHost");
        if (pill == null || overlay == null || treeBox == null) return;

        var row = Vm?.TreeRows.FirstOrDefault(r => r.IsCurrent);
        var ok = false;

        if (row != null && treeBox.ContainerFromItem(row) is Control cc && cc.Bounds.Height > 0)
        {
            var pos = cc.TranslatePoint(new Point(0, 0), overlay);
            if (pos != null)
            {
                MovePill(pill, 2, pos.Value.Y, Math.Max(24, cc.Bounds.Width - 4), cc.Bounds.Height,
                         animate: _treePillShown);
                ok = true;
            }
        }

        pill.IsVisible = ok;
        _treePillShown = ok;
        // 同上：不做自递归重试，定位失败就退回逐行填色
        host?.Classes.Set("nopill", !ok && row != null);
    }

    // 浮动高亮的"是否已在显示"状态：只有本来就显示着，才允许播滑动动画
    private bool _treePillShown;

    // 文件列表的选中滑块：Canvas 在 ScrollViewer 内容里，滚动时天然跟随
    private bool _rowPillShown;
    private Control? _filePage;

    /// <summary>
    /// 移动一块浮动高亮。
    /// animate=false → 先摘掉过渡再赋值（瞬移），用于"首次出现 / 换目录之后"，
    ///   否则会从 RenderTransform 里残留的旧值（常见是 0，也就是顶部）补间过来，
    ///   看起来就是"突然从上面滑下来"。
    /// animate=true  → 正常赋值，交给 XAML 里的 TransformOperationsTransition 做滑动。
    /// </summary>
    private static void MovePill(Border pill, double left, double top, double width, double height, bool animate)
    {
        var target = Avalonia.Media.Transformation.TransformOperations.Parse($"translateY({top:0.##}px)");

        if (!animate)
        {
            var saved = pill.Transitions;
            pill.Transitions = null;
            Canvas.SetLeft(pill, left);
            pill.Width = width;
            pill.Height = height;
            pill.RenderTransform = target;
            if (saved != null)
                Dispatcher.UIThread.Post(() => pill.Transitions = saved, DispatcherPriority.Render);
            return;
        }

        Canvas.SetLeft(pill, left);
        pill.Width = width;
        pill.Height = height;
        pill.RenderTransform = target;
    }

    private async void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm == null) return;
        var entry = Sel;
        if (entry == null) return;

        await OpenEntrySmartAsync(entry);
    }

    /// <summary>目录进入；文件按后缀自动选「打开方式」，未知后缀弹菜单。</summary>
    private async Task OpenEntrySmartAsync(PosixEntry entry)
    {
        if (Vm == null) return;
        if (entry.IsDir)
        {
            await Vm.OpenEntryAsync(entry);
            return;
        }
        // 可执行文件不直接跑：先弹「执行命令」页，让用户确认/改参数
        if (entry.IsExec)
        {
            await RunOpenWithAsync(entry, FileAction.ScriptRun);
            return;
        }

        var action = OpenWith.DefaultFor(entry.Name);
        if (action == null)
        {
            ShowContextMenu(entry);
            return;
        }
        await RunOpenWithAsync(entry, action.Value);
    }

    // ---------- Windows 设备事件：USB 插拔 <100ms 响应 ----------

    private const uint WM_DEVICECHANGE = 0x0219;
    private const int GWLP_WNDPROC = -4;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // 32 位进程里没有 SetWindowLongPtrW（只是个宏），直接调会 EntryPointNotFound 被吞掉，插拔事件永远收不到
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private IntPtr _oldWndProc = IntPtr.Zero;
    private WndProcDelegate? _wndProcDelegate;   // 必须保住引用，否则被 GC 回收会崩

    private void HookDeviceNotifications()
    {
        if (!OperatingSystem.IsWindows()) return;
        // 幂等：重复挂钩会把 _wndProcDelegate 换成新的，旧委托被 GC 后窗口过程里就是悬空函数指针
        if (_oldWndProc != IntPtr.Zero) return;
        try
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return;

            _wndProcDelegate = (h, msg, w, l) =>
            {
                if (msg == WM_DEVICECHANGE)
                {
                    try { Vm?.NotifyDeviceTreeChanged(); } catch { }
                }
                return CallWindowProc(_oldWndProc, h, msg, w, l);
            };

            var fn = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
            _oldWndProc = Environment.Is64BitProcess
                ? SetWindowLongPtr(hwnd, GWLP_WNDPROC, fn)
                : SetWindowLong32(hwnd, GWLP_WNDPROC, fn);
        }
        catch { _oldWndProc = IntPtr.Zero; }
    }

    /// <summary>关窗时把窗口过程还原，不然窗口销毁过程中还可能回调到已释放的对象</summary>
    private void UnhookDeviceNotifications()
    {
        if (_oldWndProc == IntPtr.Zero) return;
        try
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
            {
                if (Environment.Is64BitProcess) SetWindowLongPtr(hwnd, GWLP_WNDPROC, _oldWndProc);
                else SetWindowLong32(hwnd, GWLP_WNDPROC, _oldWndProc);
            }
        }
        catch { }
        finally { _oldWndProc = IntPtr.Zero; }
    }

    private async void OnAppsInstall(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要安装的 APK",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("APK 安装包") { Patterns = new[] { "*.apk" } }
            }
        });
        if (files.Count == 0) return;

        foreach (var f in files)
        {
            var path = f.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) continue;
            if (!await Vm.InstallApkAsync(path!)) break;   // 取消或失败：不再继续装下一批
        }
    }

    private async void OnCleanTraces(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;

        var pick = await ChoiceDialog.ShowAsync(this, "清理痕迹",
            "普通清理：KernelSU 日志、logcat、dmesg、临时文件、崩溃转储（只删文件，保留目录）。\n\n" +
            "深度清理：以上全做，外加删除 USB 调试授权、重置电池统计，\n" +
            "最后关闭 USB 调试和开发者选项 —— 连接会立即断开，下次使用需在手机上重新开启。",
            "普通清理", "深度清理");

        if (pick == null) return;
        var report = await Vm.CleanTracesAsync(pick == 1);
        await AppDetailDialog.ShowAsync(this, "清理完成", "以下是本次结果", report);
    }

    private void OnAppsSuperClick(object? sender, RoutedEventArgs e) { if (Vm != null) Vm.AppMode = 2; }

    private async void OnAppToggleRoot(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var item = AppOf(sender);
        if (item == null) return;

        var want = !item.IsGranted;
        item.IsGranted = want;                     // 先切 UI，失败再回滚
        await Vm.ToggleRootAsync(item, want);

        if (item.IsGranted != want) return;        // 失败，不提示重启
        if (!Vm.RootGrantNeedsReboot)              // MagiskSU 改库即生效，没必要重启
        {
            Vm.Toast($"「{item.Label}」的授权已生效");
            return;
        }

        var now = await ChoiceDialog.ShowAsync(this, "需要让改动生效",
            $"「{item.Label}」的 root 授权已写入。\n\n" +
            "软重启只重启 Android 用户界面，约 30 秒，不会丢数据，比整机重启快得多。",
            "稍后自己重启", "立即软重启");
        if (now == 1) await Vm.SoftRebootAsync();
    }

    /// <summary>应用页三个分段的滑动指示器</summary>
    private void UpdateModePill()
    {
        UpdateModePillCore();
        // 点击瞬间 .on 类刚变、布局还没重排，Bounds 仍是旧值；布局跑完再对一次
        Dispatcher.UIThread.Post(UpdateModePillCore, DispatcherPriority.Loaded);
    }

    private void UpdateModePillCore()
    {
        var pill = this.FindControl<Border>("ModePill");
        if (pill == null) return;

        var tabs = new[]
        {
            this.FindControl<Button>("ModeUser"),
            this.FindControl<Button>("ModeSys"),
            this.FindControl<Button>("ModeSuper")
        };

        var idx = Math.Clamp(Vm?.AppMode ?? 0, 0, tabs.Length - 1);
        var b = tabs[idx];
        if (b == null || b.Bounds.Width <= 0) return;

        pill.Width = Math.Max(24, b.Bounds.Width);
        pill.RenderTransform =
            Avalonia.Media.Transformation.TransformOperations.Parse($"translateX({b.Bounds.X + 2:0.##}px)");
    }

    private string _lastPath = "";
    private bool _listAnimReady;
    private Avalonia.Animation.Transitions? _fileListTransitions;

    /// <summary>
    /// 左键进入 / 返回文件夹时，给文件列表一个"滑入"过场（和标签页切换同一套观感）：
    ///   进入更深目录 → 从右侧滑入；返回上级 → 从左侧滑入；横向 30 DIP + 透明度 0.2→1，200ms。
    /// 关键手法：先摘掉 Transitions 把位置"瞬移"到起点，再恢复过渡并归零。
    ///   否则过渡会先把起点动画一遍，看起来像弹跳（原来就是这个毛病）。
    /// 首屏不播动画，避免启动时列表抖一下。
    /// </summary>
    private void AnimateFileList()
    {
        var host = this.FindControl<Panel>("FileListHost");
        if (host == null || Vm == null) return;

        _fileListTransitions ??= host.Transitions;

        // 换目录：清掉键盘游标与悬停；选中态由新列表重新建立
        HideRowPill();
        _selCursor = -1;
        _selAnchor = -1;
        _hoverEntry = null;
        SetHoverBorder(null);

        var path = Vm.CurrentPath;
        var first = !_listAnimReady;
        _listAnimReady = true;

        var deeper = path.Length >= _lastPath.Length &&
                     path.StartsWith(_lastPath, StringComparison.OrdinalIgnoreCase);
        _lastPath = path;

        // 首屏只淡入（不横移），之后进入更深目录从右滑、返回上级从左滑
        var dx = first ? 0 : (deeper ? 24 : -24);

        host.Transitions = null;                       // 这一步不要动画
        host.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse($"translateX({dx}px)");
        host.Opacity = first ? 0.55 : 0.4;

        Dispatcher.UIThread.Post(() =>
        {
            host.Transitions = _fileListTransitions;    // 恢复 XAML 里的过渡，再归零 → 产生滑入
            host.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translateX(0px)");
            host.Opacity = 1;
        }, DispatcherPriority.Render);
    }

    private static AppItem? AppOf(object? sender) => (sender as Control)?.DataContext as AppItem;

    private async void OnAppDetail(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var item = AppOf(sender);
        if (item == null) return;

        Vm.Toast("正在读取应用详情…");
        var rows = await Vm.GetAppDetailsAsync(item);
        await AppDetailDialog.ShowAsync(this, item.Label, item.Package, rows);
    }

    private async void OnAppUninstall(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var item = AppOf(sender);
        if (item == null) return;

        var tip = item.IsSystem
            ? $"「{item.Label}」是系统应用，无法真正卸载。\n\n是否改为「停用」（对当前用户隐藏）？"
            : $"确定卸载「{item.Label}」吗？\n\n包名：{item.Package}\n\n应用数据会一并删除。";

        var ok = await ConfirmDialog.ShowAsync(this, item.IsSystem ? "停用系统应用" : "卸载应用", tip);
        if (!ok) return;

        await Vm.UninstallAppAsync(item);
    }

    private async void OnAppExtract(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var item = AppOf(sender);
        if (item == null) return;

        var choice = await ChoiceDialog.ShowAsync(this, "提取安装包",
            $"把「{item.Label}」的安装包保存到哪里？", "保存到手机", "保存到电脑");

        if (choice == null) return;

        if (choice == 1)
        {
            // 电脑：Windows 原生保存对话框
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存安装包",
                SuggestedFileName = item.Package + ".apk",
                DefaultExtension = "apk",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("APK 安装包") { Patterns = new[] { "*.apk" } }
                }
            });
            if (file == null) return;
            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) { Vm.Toast("请选择本地磁盘位置"); return; }
            await Vm.ExtractApkToPcAsync(item, path!);
        }
        else
        {
            // 手机：应用内目录选择器
            Vm.Toast("正在读取目录…");
            var dir = await FolderPickerDialog.ShowAsync(this, "选择手机上的保存目录",
                DevicePaths.Download, p => Vm.ListSubDirsAsync(p));
            if (string.IsNullOrEmpty(dir)) return;
            await Vm.ExtractApkToPhoneAsync(item, dir!);
        }
    }

    private void OnAppsUserClick(object? sender, RoutedEventArgs e) { if (Vm != null) Vm.AppMode = 0; }
    private void OnAppsSystemClick(object? sender, RoutedEventArgs e) { if (Vm != null) Vm.AppMode = 1; }
    private void OnAppsRefresh(object? sender, RoutedEventArgs e) { if (Vm != null) _ = Vm.LoadAppsAsync(); }

    // ================= 压缩包 =================

    private async void OnArchiveSave(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var ok = await ConfirmDialog.ShowAsync(this, "保存到压缩包",
            $"把当前临时目录的改动写回：\n{Vm.ArchiveName}\n\n原压缩包会被覆盖。");
        if (!ok) return;
        await Vm.SaveArchiveAsync();
    }

    private async void OnArchiveClose(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var ok = await ConfirmDialog.ShowAsync(this, "退出压缩包编辑",
            "退出会丢弃未保存的改动，并删除临时文件。\n确定退出吗？");
        if (!ok) return;
        await Vm.CloseArchiveAsync();
    }

    private async Task ExtractArchiveAsync(PosixEntry e)
    {
        if (Vm == null) return;
        var def = Path.GetFileNameWithoutExtension(e.Name);
        var name = await InputDialog.ShowAsync(this, "解压到文件夹", "文件夹名称（解压到当前目录下）", def);
        if (string.IsNullOrWhiteSpace(name)) return;
        await Vm.ExtractArchiveAsync(e, name!.Trim());
    }

    // ================= 拖拽反馈（对齐资源管理器的观感） =================

    private string _overlayText = "";

    private void ShowDropOverlay(string text)
    {
        var ov = this.FindControl<Grid>("DropOverlay");
        var tb = this.FindControl<TextBlock>("DropOverlayText");
        if (ov == null) return;
        // 每次 DragOver 都改文本/可见性会让浮层反复重排 → 拖拽时闪。
        //   只有内容真的变了才动 UI。
        if (tb != null && !string.IsNullOrEmpty(text) && text != _overlayText)
        {
            tb.Text = text;
            _overlayText = text;
        }
        if (!ov.IsVisible) ov.IsVisible = true;
    }

    private void HideDropOverlay()
    {
        var ov = this.FindControl<Grid>("DropOverlay");
        if (ov != null && ov.IsVisible) ov.IsVisible = false;
    }

    private void ShowDragGhost(string text, Point windowPos)
    {
        var ghost = this.FindControl<Border>("DragGhost");
        var layer = this.FindControl<Canvas>("DragGhostLayer");
        var label = this.FindControl<TextBlock>("DragGhostText");
        if (ghost == null || layer == null || label == null) return;

        label.Text = text;
        ghost.IsVisible = true;

        // 不让提示跑到窗口外面
        var x = Math.Min(Math.Max(4, windowPos.X + 16), Math.Max(4, Bounds.Width - 320));
        var y = Math.Min(Math.Max(4, windowPos.Y + 16), Math.Max(4, Bounds.Height - 40));
        Canvas.SetLeft(ghost, x);
        Canvas.SetTop(ghost, y);
    }

    private void HideDragGhost()
    {
        var ghost = this.FindControl<Border>("DragGhost");
        if (ghost != null) ghost.IsVisible = false;
    }

    // 从资源管理器拖进窗口：落点不一定在列表上，所以整页都要能收
    private void OnPageDragOver(object? sender, DragEventArgs e)
    {
        if (Vm == null) return;
        if (e.Data.Contains(InternalFormat)) { e.DragEffects = DragDropEffects.None; e.Handled = true; return; }

        var ok = e.Data.Contains(DataFormats.Files) && !e.Data.Contains(InternalFormat);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        ShowUploadBar(ok);
        if (ok)
        {
            ShowDropOverlay("松开即上传到当前目录");
            SetDragHint("松开即上传到当前目录");
        }
        else
        {
            HideDropOverlay();
            SetDragHint("");
        }
        e.Handled = true;
    }

    private void ShowUploadBar(bool show)
    {
        var bar = this.FindControl<Border>("UploadBar");
        if (bar != null && bar.IsVisible != show) bar.IsVisible = show;
    }

    /// <summary>指针是否在"上传到当前目录"落点条上（条本身不接收拖拽事件，避免它出现/消失导致事件来回跳）。</summary>
    private bool IsOverUploadBar(DragEventArgs e)
    {
        var bar = this.FindControl<Border>("UploadBar");
        if (bar == null || !bar.IsVisible || bar.Bounds.Width <= 0) return false;
        var p = e.GetPosition(bar);
        return p.X >= 0 && p.Y >= 0 && p.X <= bar.Bounds.Width && p.Y <= bar.Bounds.Height;
    }

    private void SetDragHint(string text)
    {
        if (Vm != null && Vm.DragHint != text) Vm.DragHint = text;
    }

    private async void OnPageDrop(object? sender, DragEventArgs e)
    {
        HideDropOverlay();
        ShowUploadBar(false);
        SetDragHint("");
        if (Vm == null) return;
        var files = e.Data.GetFiles();
        if (files == null) return;

        var locals = files.Select(x => x.TryGetLocalPath()).Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToList();
        if (locals.Count == 0) { e.Handled = true; return; }

        e.Handled = true;
        await Vm.UploadToAsync(locals, Vm.CurrentPath);
    }

    private void OnPageDragLeave(object? sender, DragEventArgs e)
    {
        HideDropOverlay();
        ShowUploadBar(false);
        SetTreeHighlight(null);
        SetHighlight(null);
        SetDragHint("");
    }

    // ================= 上传 / 下载 =================

    private async void OnUploadClick(object? sender, RoutedEventArgs e) => await UploadToAsync(Vm?.CurrentPath);

    private async Task UploadToAsync(string? targetDir)
    {
        if (Vm == null || string.IsNullOrEmpty(targetDir)) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要放进手机的文件",
            AllowMultiple = true,
        });
        var paths = files.Select(f => f.TryGetLocalPath())
                         .Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        if (paths.Count == 0) return;
        await Vm.UploadToAsync(paths, targetDir);
    }

    private async Task BatchDownloadAsync()
    {
        if (Vm == null) return;
        var items = SelectedEntries();
        if (items.Count == 0) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择保存位置",
            AllowMultiple = false
        });
        var dir = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(dir)) return;

        await Vm.BatchDownloadAsync(items, dir!);
    }

    private async void OnDownloadClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var sel = Sel ?? SelectedEntries().FirstOrDefault();
        if (sel == null) { Vm.StatusText = "请先选中一个文件，再点下载"; return; }
        if (sel.IsDir) { Vm.StatusText = "文件夹请用「拖出」或右键「打包下载」"; return; }

        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存到电脑",
            SuggestedFileName = sel.Name,
        });
        var local = target?.TryGetLocalPath();
        if (string.IsNullOrEmpty(local)) return;
        await Vm.DownloadAsync(sel, local);
    }

    // ================= 执行 =================

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var sel = Sel ?? SelectedEntries().FirstOrDefault();
        var filePath = sel?.FullPath ?? "";
        var initial = sel == null
            ? "pwd"
            : (sel.IsDir ? "ls -lZ " + Quote(sel.FullPath) : ExecCommandFor(sel));

        await RunDialog.ShowAsync(this, filePath, initial, Vm.IsRoot, Vm.IsRoot,
            async (cmd, root) => await Vm.RunCommandAsync(cmd, root),
            async code => { if (Vm != null) await Vm.SendKeyAsync(code); });
    }

    /// <summary>脚本用 sh 跑；ELF/二进制直接执行；其它文件交给 sh（会给出明确报错）。</summary>
    private static string ExecCommandFor(PosixEntry e)
    {
        var ext = OpenWith.Ext(e.Name);
        return ext is "sh" or "bash" or "zsh" or "run"
            ? "sh " + Quote(e.FullPath)
            : Quote(e.FullPath);
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    // ================= 删除 / 重命名 / 新建 =================

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var items = SelectedEntries();
        if (items.Count == 0) { Vm.StatusText = "请先选中要删除的项目"; return; }

        var names = string.Join("\n", items.Take(8).Select(i => "· " + i.Name));
        if (items.Count > 8) names += $"\n… 共 {items.Count} 项";
        var ok = await ConfirmDialog.ShowAsync(this, "确认删除",
            $"以下内容会被永久删除，无法恢复：\n\n{names}\n\n确定继续吗？");
        if (!ok) return;
        await Vm.DeleteAsync(items);
    }

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var sel = Sel;
        if (sel == null) { Vm.StatusText = "请先选中一个项目"; return; }
        var name = await InputDialog.ShowAsync(this, "重命名", "请输入新的名称：", sel.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        await Vm.RenameAsync(sel, name!);
    }

    private async void OnNewFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var name = await InputDialog.ShowAsync(this, "新建文件夹", "请输入文件夹名称：", "新建文件夹");
        if (string.IsNullOrWhiteSpace(name)) return;
        await Vm.NewFolderAsync(name!);
    }

    private async void OnNewFileClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var name = await InputDialog.ShowAsync(this, "新建文件", "请输入文件名（可带扩展名）：", "new.txt");
        if (string.IsNullOrWhiteSpace(name)) return;
        await Vm.NewFileAsync(name!);
    }

    // ================= 权限 / 编辑 =================

    private async void OnPermissionClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var sel = Sel ?? SelectedEntries().FirstOrDefault();
        if (sel == null) { Vm.StatusText = "请先选中一个项目"; return; }
        var mode = await PermissionDialog.ShowAsync(this, sel.FullPath, sel.ModeOctal);
        if (string.IsNullOrWhiteSpace(mode)) return;
        await Vm.ApplyModeAsync(sel, mode!);
    }

    private async void OnEditClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var sel = Sel ?? SelectedEntries().FirstOrDefault();
        if (sel == null || sel.IsDir) { Vm.StatusText = "请选中一个文本文件"; return; }

        string content;
        try { content = await Vm.ReadTextAsync(sel); }
        catch (Exception ex) { Vm.StatusText = "读取失败：" + ex.Message; return; }
        if (content.Length > 2_000_000) { Vm.StatusText = "文件超过 2MB，请改用下载"; return; }

        var result = await TextEditorWindow.ShowAsync(this, sel.FullPath, content);
        if (result == null) return;
        await Vm.WriteTextAsync(sel, result);
    }

    // ================= SELinux =================

    private async void OnRestoreconClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var items = SelectedEntries();
        if (items.Count == 0) { Vm.StatusText = "请先选中项目"; return; }

        var target = items[0];
        string preview;
        try { preview = await Vm.RestoreconPreviewAsync(target); }
        catch (Exception ex) { Vm.StatusText = "预览失败：" + ex.Message; return; }

        var msg = string.IsNullOrWhiteSpace(preview)
            ? $"「{target.Name}」的 SELinux 标签已经符合系统策略，不需要修改。"
            : $"下面这些文件会被改回系统默认标签：\n\n{Trim(preview, 1800)}\n\n确定执行吗？";

        var ok = await ConfirmDialog.ShowAsync(this, "修复 SELinux 标签", msg);
        if (!ok) return;

        var result = await Vm.RestoreconApplyAsync(target);
        Vm.StatusText = string.IsNullOrWhiteSpace(result) ? "SELinux 标签已修复" : "SELinux： " + FirstLine(result);
    }

    private async void OnChconClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var sel = Sel;
        if (sel == null) { Vm.StatusText = "请先选中一个项目"; return; }

        var type = await InputDialog.ShowAsync(this, "手动设置 SELinux 类型（高级）",
            $"当前标签：{sel.Selinux}\n\n请输入类型名，例如 adb_data_file / system_data_file：", "");
        if (string.IsNullOrWhiteSpace(type)) return;

        var ok = await ConfirmDialog.ShowAsync(this, "危险操作确认",
            $"即将把\n{sel.FullPath}\n\n的 SELinux 类型改为\n{type}\n\n类型写错可能导致该文件无法被系统访问。确定继续吗？");
        if (!ok) return;

        var result = await Vm.ChconAsync(sel, type!.Trim());
        Vm.StatusText = string.IsNullOrWhiteSpace(result) ? "标签已设置" : FirstLine(result);
    }

    // ================= 右键菜单（按落点动态生成） =================

    private async void OnPropertyClick(object? sender, RoutedEventArgs e)
    {
        var sel = Sel ?? SelectedEntries().FirstOrDefault();
        if (sel == null) { if (Vm != null) Vm.StatusText = "请先选中一个项目，再点属性"; return; }
        await ShowPropertyAsync(sel);
    }

    private async Task ShowPropertyAsync(PosixEntry e) => await PropertyDialog.ShowAsync(this, e);

    /// <summary>「打开方式」子菜单：分组 + 每项独立图标/颜色。</summary>
    private MenuItem BuildOpenWithMenu(PosixEntry entry)
    {
        var root = new MenuItem { Header = "打开方式", Icon = MenuIcons.Create("open") };

        // 18 项单列会顶到屏幕外且不好点：改成两列网格（9 行），高度约减半。
        root.ItemsPanel = new FuncTemplate<Panel?>(() => new UniformGrid { Columns = 2 });

        var order = new[]
        {
            FileAction.EditText, FileAction.XmlInfo,
            FileAction.SvgInfo, FileAction.ImagePreview,
            FileAction.MusicPlay, FileAction.VideoPlay,
            FileAction.FontPreview, FileAction.ArchiveBrowse,
            FileAction.JarInfo, FileAction.ApkInfo,
            FileAction.ApksInfo, FileAction.SignatureInfo,
            FileAction.HexView, FileAction.DexInfo,
            FileAction.ArscInfo, FileAction.ClassInfo,
            FileAction.NinePatchPreview, FileAction.ScriptRun,
        };

        var list = new List<Control>();
        foreach (var action in order)
        {
            var brush = new SolidColorBrush(Color.Parse(OpenWith.Color(action)));
            var mi = new MenuItem
            {
                Header = OpenWith.Title(action),
                Icon = MenuIcons.Create(OpenWith.IconKey(action), brush),
                MinWidth = 178,
            };
            var a = action;
            mi.Click += (_, _) => _ = RunOpenWithAsync(entry, a);
            list.Add(mi);
        }
        root.ItemsSource = list;
        if (MenuDebug)
            AppLog.Add("OPENWITH " + string.Join(" | ", list.OfType<MenuItem>().Select(m => m.Header)));
        return root;
    }

    /// <summary>按「打开方式」打开一个远端文件。</summary>
    private async Task RunOpenWithAsync(PosixEntry e, FileAction action)
    {
        if (Vm == null) return;
        try
        {
            switch (action)
            {
                case FileAction.EditText:
                    OnEditClick(null, null!);
                    return;
                case FileAction.ArchiveBrowse:
                    if (!await Vm.OpenArchiveAsync(e))
                        Vm.StatusText = "无法作为压缩包打开（可能不是 zip，或压缩包已加密）";
                    return;
                case FileAction.ScriptRun:
                    await RunDialog.ShowAsync(this, e.FullPath, ExecCommandFor(e), Vm.IsRoot, Vm.IsRoot,
                        async (cmd, root) => await Vm.RunCommandAsync(cmd, root),
                        async code => { if (Vm != null) await Vm.SendKeyAsync(code); });
                    return;
                case FileAction.MusicPlay:
                case FileAction.VideoPlay:
                {
                    Vm.StatusText = "正在下载，准备交给系统播放器…";
                    var local = await Vm.DownloadToTempAsync(e);
                    if (string.IsNullOrEmpty(local)) { Vm.StatusText = "下载失败"; return; }
                    Process.Start(new ProcessStartInfo(local!) { UseShellExecute = true });
                    Vm.StatusText = "已用系统播放器打开";
                    return;
                }
                case FileAction.ApkInfo:
                case FileAction.ApksInfo:
                case FileAction.SignatureInfo:
                case FileAction.JarInfo:
                {
                    Vm.StatusText = "正在读取 " + e.Name + " …";
                    var local = await Vm.DownloadToTempAsync(e);
                    if (string.IsNullOrEmpty(local)) { Vm.StatusText = "下载失败"; return; }
                    try
                    {
                        string text;
                        if (action == FileAction.ApksInfo)
                            text = await BuildApksReportAsync(local!);
                        else if (action == FileAction.JarInfo)
                            text = OpenWith.ZipReport(local!, "JAR");
                        else
                            text = await Task.Run(() => OpenWith.ApkReport(local!, action == FileAction.SignatureInfo));
                        await FileViewerWindow.ShowTextAsync(this, OpenWith.Title(action) + " · " + e.Name, text);
                    }
                    finally { try { File.Delete(local!); } catch { } }
                    return;
                }
            }

            var bytes = await Vm.ReadBytesAsync(e);
            if (bytes.Length == 0) { Vm.StatusText = "读取失败或文件为空"; return; }

            switch (action)
            {
                case FileAction.HexView:
                    await FileViewerWindow.ShowTextAsync(this, "十六进制 · " + e.Name, OpenWith.HexDump(bytes));
                    break;
                case FileAction.XmlInfo:
                    await FileViewerWindow.ShowTextAsync(this, "Xml · " + e.Name, OpenWith.XmlReport(bytes));
                    break;
                case FileAction.SvgInfo:
                    await FileViewerWindow.ShowTextAsync(this, "Svg · " + e.Name, OpenWith.TextReport(bytes));
                    break;
                case FileAction.DexInfo:
                    await FileViewerWindow.ShowTextAsync(this, "Dex · " + e.Name, OpenWith.DexReport(bytes, e.Name));
                    break;
                case FileAction.ArscInfo:
                    await FileViewerWindow.ShowTextAsync(this, "Arsc · " + e.Name, OpenWith.ArscReport(bytes));
                    break;
                case FileAction.ClassInfo:
                    await FileViewerWindow.ShowTextAsync(this, "Class · " + e.Name, OpenWith.ClassReport(bytes));
                    break;
                case FileAction.ImagePreview:
                    await FileViewerWindow.ShowImageAsync(this, "图片 · " + e.Name, bytes);
                    break;
                case FileAction.NinePatchPreview:
                    await FileViewerWindow.ShowImageAsync(this, "点九 · " + e.Name, bytes, true);
                    break;
                case FileAction.FontPreview:
                    await FileViewerWindow.ShowFontAsync(this, "字体 · " + e.Name, bytes);
                    break;
            }
        }
        catch (Exception ex)
        {
            Vm.StatusText = OpenWith.Title(action) + "失败：" + ex.Message;
        }
    }

    /// <summary>APKS/XAPK：列出条目，并抽第一个 APK 做完整分析。</summary>
    private static async Task<string> BuildApksReportAsync(string local)
    {
        var sb = new StringBuilder();
        try
        {
            using var zip = ZipFile.OpenRead(local);
            sb.AppendLine($"包内条目：{zip.Entries.Count} 个");
            foreach (var x in zip.Entries.OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase).Take(300))
                sb.AppendLine($"{x.Length,12:N0}  {x.FullName}");
            var apk = zip.Entries
                .Where(x => x.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.FullName.Length)
                .FirstOrDefault();
            if (apk != null)
            {
                var tmp = Path.Combine(Path.GetTempPath(), "dashfile_apks_" + Guid.NewGuid().ToString("N")[..8] + ".apk");
                try
                {
                    apk.ExtractToFile(tmp, true);
                    sb.AppendLine();
                    sb.AppendLine("── 包内 APK 分析：" + apk.FullName + " ──");
                    sb.Append(await Task.Run(() => OpenWith.ApkReport(tmp)));
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
        }
        catch (Exception ex) { sb.AppendLine("读取失败：" + ex.Message); }
        return sb.ToString();
    }

    private void ShowContextMenu(PosixEntry? entry)
    {
        if (Vm == null) return;

        var isDir = entry is { IsDir: true };
        var isFile = entry != null && !entry.IsDir;

        // 右键点在某一行上 → 选中它（多选时保留已选）
        if (entry != null && Files.SelectedItems?.Contains(entry) != true)
        {
            Files.SelectedItems?.Clear();
            Files.SelectedItem = entry;
        }

        var items = new List<Control>();

        void Add(string header, Action act)
        {
            var mi = MenuIcons.Item(header);
            mi.Click += (_, __) => act();
            items.Add(mi);
        }
        void Sep()
        {
            if (items.Count > 0 && items[^1] is not Separator) items.Add(new Separator());
        }

        if (entry == null)
        {
            // 空白处：只给"往这个目录里放东西"相关的
            if (Vm.HasClipboard) Add("粘贴到当前目录", () => _ = Vm.PasteAsync());
            Add("上传文件到当前目录…", () => _ = UploadToAsync(Vm.CurrentPath));
            Add("新建文件夹", () => OnNewFolderClick(null, null!));
            Add("新建文件", () => OnNewFileClick(null, null!));
            Sep();
            Add("刷新", () => _ = Vm.RefreshAsync());
            Add("在命令行里打开此目录", () => _ = OpenRunDialogForDir());
        }
        else if (isDir)
        {
            Add("打开 / 进入", () => _ = Vm.OpenEntryAsync(entry));
            Add("把文件上传到这个文件夹…", () => _ = UploadToAsync(entry.FullPath));
            Sep();
            Add("复制", () => Vm.CopyToClipboard(SelectedEntries()));
            Add("打包下载 (.tar.gz)", () => _ = DownloadFolderAsync(entry));
            Sep();
            Add("重命名", () => OnRenameClick(null, null!));
            Add("删除", () => OnDeleteClick(null, null!));
            Sep();
            Add("修改权限…", () => OnPermissionClick(null, null!));
            Add("修复 SELinux 标签", () => OnRestoreconClick(null, null!));
            Add("手动设 SELinux 类型…", () => OnChconClick(null, null!));
            Sep();
            Add("属性", () => _ = ShowPropertyAsync(entry));
            Add("刷新", () => _ = Vm.RefreshAsync());
        }
        else
        {
            if (entry.IsZip)
            {
                Add("解压到文件夹…", () => _ = ExtractArchiveAsync(entry));
                Add("打开压缩包（可编辑后保存）", () => _ = Vm.OpenArchiveAsync(entry));
                Sep();
            }
            Add("复制", () => Vm.CopyToClipboard(SelectedEntries()));
            var selCount = SelectedEntries().Count;
            if (selCount > 1)
                Add($"下载选中 {selCount} 项到文件夹…", () => _ = BatchDownloadAsync());
            Add("下载到电脑…", () => OnDownloadClick(null, null!));
            Add("执行…", () => OnRunClick(null, null!));
            Sep();
            Add("重命名", () => OnRenameClick(null, null!));
            Add("删除", () => OnDeleteClick(null, null!));
            Sep();
            Add("修改权限…", () => OnPermissionClick(null, null!));
            items.Add(BuildOpenWithMenu(entry));
            Sep();
            Add("修复 SELinux 标签", () => OnRestoreconClick(null, null!));
            Add("手动设 SELinux 类型…", () => OnChconClick(null, null!));
            Sep();
            Add("属性", () => _ = ShowPropertyAsync(entry));
            Add("刷新", () => _ = Vm.RefreshAsync());
        }

        // 插件动作：文件 / 目录分别注入，空白处按当前目录处理
        if (entry == null)
            AppendPluginMenuItems(items, PluginTargetKind.Directory,
                PluginTarget.ForDirectory(Vm.CurrentPath, Vm.CurrentPath));
        else if (isDir)
            AppendPluginMenuItems(items, PluginTargetKind.Directory,
                PluginTarget.ForDirectory(entry.FullPath, entry.Name));
        else
            AppendPluginMenuItems(items, PluginTargetKind.File,
                PluginTarget.ForFile(entry.FullPath, entry.Name));

        while (items.Count > 0 && items[^1] is Separator) items.RemoveAt(items.Count - 1);
        var menu = MenuFor(Files);
        menu.ItemsSource = items;
        menu.Open(Files);
    }

    private async Task OpenRunDialogForDir()
    {
        if (Vm == null) return;
        var cmd = "ls -lZ " + Quote(Vm.CurrentPath);
        await RunDialog.ShowAsync(this, "", cmd, Vm.IsRoot, Vm.IsRoot,
            async (c, root) => await Vm.RunCommandAsync(c, root));
    }

    private async Task DownloadFolderAsync(PosixEntry dir)
    {
        if (Vm == null) return;
        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存文件夹压缩包到电脑",
            SuggestedFileName = dir.Name + ".tar.gz",
        });
        var local = target?.TryGetLocalPath();
        if (string.IsNullOrEmpty(local)) return;

        try
        {
            Vm.StatusText = "正在打包：" + dir.Name;
            var remoteTar = DevicePaths.TempFile("export", ".tar.gz");
            await Vm.PackAndDownloadAsync(dir, remoteTar, local);
            Vm.StatusText = "已保存：" + local;
        }
        catch (Exception ex)
        {
            Vm.StatusText = "打包下载失败：" + ex.Message;
        }
    }

    // ================= 拖入（电脑 → 手机） =================

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // 只处理“从电脑拖进来”的上传；列表内部的拖动走指针事件，不走系统拖放
        var ok = e.Data.Contains(DataFormats.Files);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;

        var external = ok && !e.Data.Contains(InternalFormat);
        ShowUploadBar(external);
        if (ok)
        {
            var overBar = IsOverUploadBar(e);
            var hit = overBar ? null : HitTestEntry(e.GetPosition(Files));
            if (hit is { IsDir: true }) SetHighlight(hit);
            else SetHighlight(null);

            var text = overBar
                ? "松开：上传到当前目录"
                : hit is { IsDir: true } ? $"松开即放进「{hit.Name}」" : "松开即上传到当前目录";
            ShowDropOverlay(text);
            SetDragHint(text);
        }
        else
        {
            SetHighlight(null);
            HideDropOverlay();
            SetDragHint("");
        }
        e.Handled = true;
    }

    private void OnFilesDragLeave(object? sender, DragEventArgs e)
    {
        // 离开列表（移动到工具栏/底部条）时清掉行高亮，避免高亮停在原处
        SetHighlight(null);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;      // 先拦下，避免 await 期间同一批文件继续冒泡到 OnPageDrop 被上传两次
        HideDropOverlay();
        ShowUploadBar(false);
        SetHighlight(null);
        if (Vm == null) return;
        var items = e.Data.GetFiles();
        if (items == null) return;

        var localPaths = items.Select(i => i.TryGetLocalPath())
                         .Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        if (localPaths.Count == 0) return;

        var hit = IsOverUploadBar(e) ? null : HitTestEntry(e.GetPosition(Files));
        var dir = hit is { IsDir: true } ? hit.FullPath : Vm.CurrentPath;
        await Vm.UploadToAsync(localPaths, dir);
    }

    /// <summary>拖拽时高亮目标行</summary>
    private PosixEntry? _hlEntry;

    private void SetHighlight(PosixEntry? entry)
    {
        // 每个 DragOver 都清一次再设一次，会让目标行逐帧重绘（拖拽时闪烁）。
        //   目标没变就直接返回。
        if (ReferenceEquals(entry, _hlEntry)) return;
        _hlEntry = entry;
        if (_hlItem != null) { _hlItem.ClearValue(ListBoxItem.BackgroundProperty); _hlItem = null; }
        if (entry == null) return;
        if (Files.ContainerFromItem(entry) is ListBoxItem li)
        {
            // 只走主题资源，不在代码里写死颜色（否则切主题时高亮不跟随）
            if (Application.Current != null &&
                Application.Current.TryGetResource("CardSel", null, out var res) &&
                res is IBrush brush)
            {
                li.Background = brush;
                _hlItem = li;
            }
        }
    }

    // ================= 指针：按下 / 移动 / 松开 =================

    private Border? _hoverBorder;
    private PosixEntry? _hoverEntry;
    private static readonly bool HoverDebug =
        string.Equals(Environment.GetEnvironmentVariable("DASHFILE_HOVER_DEBUG"), "1", StringComparison.Ordinal);
    private static readonly bool MenuDebug =
        string.Equals(Environment.GetEnvironmentVariable("DASHFILE_MENU_DEBUG"), "1", StringComparison.Ordinal);
    private int _hoverMoves;

    /// <summary>代码驱动的文件行悬停：保证鼠标每次移动都换行，离开/滚动/开始拖动就清掉。</summary>
    private void UpdateFileHover(Point pos)
    {
        var entry = HitTestEntry(pos);
        if (!ReferenceEquals(entry, _hoverEntry))
        {
            if (HoverDebug)
                AppLog.Add($"HOVER {_hoverEntry?.Name ?? "null"} -> {entry?.Name ?? "null"} " +
                           $"pos={pos.X:0},{pos.Y:0} sel={Files.SelectedItems?.Count ?? 0}");
            _hoverEntry = entry;
        }
        else if (entry == null)
        {
            if (_hoverBorder != null) SetHoverBorder(null);
            return;
        }
        else if (_hoverBorder != null &&
                 ReferenceEquals(_hoverBorder.DataContext, entry) &&
                 _hoverBorder.Classes.Contains("hover"))
        {
            // 快速路径：行没变、类还在，绝大多数鼠标移动都不需要再做命中/容器查询
            return;
        }

        // 行容器可能被重建/回收：只有需要时才重新解析，避免每次移动都扫视觉树
        var border = entry == null ? null : RowBorderOf(entry);
        if (ReferenceEquals(border, _hoverBorder))
        {
            if (HoverDebug && border != null && !border.Classes.Contains("hover"))
                AppLog.Add($"HOVER class lost on {entry!.Name}");
            return;
        }
        SetHoverBorder(border);
    }

    private Border? RowBorderOf(PosixEntry entry)
        => Files.ContainerFromItem(entry) is ListBoxItem li
            ? li.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("row"))
            : null;

    private void SetHoverBorder(Border? border)
    {
        if (ReferenceEquals(border, _hoverBorder)) return;
        var old = _hoverBorder;
        _hoverBorder = border;
        if (old != null) old.Classes.Set("hover", false);
        if (border != null)
        {
            border.Classes.Set("hover", true);
            return;
        }
        // 只在"清空"时扫一遍：容器回收后 .hover 可能留在旧 Border 上
        foreach (var li in Files.GetRealizedContainers())
        {
            if (li is not ListBoxItem item) continue;
            foreach (var b in item.GetVisualDescendants().OfType<Border>())
            {
                if (b.Classes.Contains("row") && b.Classes.Contains("hover"))
                    b.Classes.Set("hover", false);
            }
        }
    }

    private void ClearFileHover()
    {
        _hoverEntry = null;
        if (_hoverBorder != null) _hoverBorder.Classes.Set("hover", false);
        SetHoverBorder(null);
    }

    /// <summary>窗口级指针跟踪：指针在列表内就更新悬停，离开列表/窗口就清掉。</summary>
    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_rowDrag) return;
        // 其它页面（设备/应用/模块…）不需要跟悬停，省掉每次鼠标移动的命中测试
        if (_filePage is { IsVisible: false }) return;
        var pos = e.GetPosition(Files);
        if (pos.X < 0 || pos.Y < 0 || pos.X > Files.Bounds.Width || pos.Y > Files.Bounds.Height)
        {
            ClearFileHover();
            return;
        }
        UpdateFileHover(pos);
    }

    private void OnFilesPointerExited(object? sender, PointerEventArgs e) => ClearFileHover();

    private void OnFilesPointerPressed(object? sender, PointerEventArgs e)
    {
        var pt = e.GetCurrentPoint(Files);
        var entry = HitTestEntry(pt.Position);

        if (pt.Properties.IsRightButtonPressed)
        {
            _rightPos = pt.Position;
            ShowContextMenu(entry);
            e.Handled = true;
            return;
        }

        if (!pt.Properties.IsLeftButtonPressed) return;

        // 多选模式：单击的"加选/取消"延到松手再判断，这样仍能直接把已选项拖走
        if (_multiMode && entry != null)
        {
            _pressEntry = entry;
            _pressPos = pt.Position;
            _dragArmed = true;
            _pendingToggle = entry;
            e.Handled = true;
            return;
        }

        _pressEntry = entry;
        _pressPos = pt.Position;
        _dragArmed = entry != null;

        if (entry != null && !_multiMode) StartHoldTimer(entry);
        else CancelHold();
    }

    private void OnFilesPointerMoved(object? sender, PointerEventArgs e)
    {
        if (HoverDebug && ++_hoverMoves % 20 == 0)
            AppLog.Add($"HOVER move#{_hoverMoves} drag={_rowDrag} armed={_dragArmed}");

        if (_rowDrag)
        {
            UpdateRowDrag(e.GetPosition(Files));
            return;
        }

        if (!_dragArmed) return;
        var pt = e.GetCurrentPoint(Files);
        if (!pt.Properties.IsLeftButtonPressed) { _dragArmed = false; return; }

        var p = pt.Position;
        if (Math.Abs(p.X - _pressPos.X) < 6 && Math.Abs(p.Y - _pressPos.Y) < 6) return;

        _dragArmed = false;
        CancelHold();
        _hoverEntry = null;
        SetHoverBorder(null);
        BeginRowDrag(e.Pointer);
    }

    private void OnFilesPointerReleased(object? sender, PointerEventArgs e)
    {
        _dragArmed = false;
        // 点击选中后指针可能没再移动过：立刻按当前落点重算一次，避免沿用按下前的高亮
        UpdateFileHover(e.GetPosition(Files));

        if (_rowDrag)
        {
            _rowDrag = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            _ = FinishRowDragAsync(e);
            return;
        }

        if (_pendingToggle != null)
        {
            var t = _pendingToggle;
            _pendingToggle = null;
            ToggleMulti(t);
            e.Handled = true;
            return;
        }

        CancelHold();
    }

    // ================= 长按多选 =================

    private void StartHoldTimer(PosixEntry entry)
    {
        CancelHold();
        _holdEntry = entry;
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _holdTimer.Tick += (_, _) =>
        {
            if (_holdTimer != null) { _holdTimer.Stop(); _holdTimer = null; }
            _dragArmed = false;          // 长按已触发，不再进入拖动
            EnterMultiSelect(_holdEntry);
        };
        _holdTimer.Start();
    }

    private void CancelHold()
    {
        if (_holdTimer != null) { _holdTimer.Stop(); _holdTimer = null; }
        _holdEntry = null;
    }

    private void EnterMultiSelect(PosixEntry? entry)
    {
        if (Vm == null) return;
        _multiMode = true;
        Files.SelectionMode = SelectionMode.Multiple;
        if (entry != null && Files.SelectedItems != null && !Files.SelectedItems.Contains(entry))
            Files.SelectedItems.Add(entry);
        Vm.IsMultiSelect = true;
        UpdateMultiHint();
    }

    private void ToggleMulti(PosixEntry entry)
    {
        var sel = Files.SelectedItems;
        if (sel == null) return;
        if (sel.Contains(entry)) sel.Remove(entry);
        else sel.Add(entry);
        Vm?.UpdateSelectionInfo(SelectedEntries());
        UpdateMultiHint();
    }

    private void ExitMultiSelect()
    {
        _multiMode = false;
        Files.SelectionMode = SelectionMode.Multiple;
        if (Vm != null) { Vm.IsMultiSelect = false; Vm.MultiSelectHint = ""; }
    }

    private void OnExitMultiSelect(object? sender, RoutedEventArgs e) => ExitMultiSelect();

    private void UpdateMultiHint()
    {
        if (Vm == null) return;
        var n = Files.SelectedItems?.Count ?? 0;
        Vm.MultiSelectHint = $"多选模式 · 已选 {n} 项（单击可继续加选，再点一次取消）";
    }

    // ================= 行拖动：落点在内部文件夹=移动，落点在窗口外=导出 =================

    private bool _rowDrag;
    private List<PosixEntry> _rowDragItems = new();
    private PosixEntry? _dragTarget;

    /// <summary>
    /// 拖拽光标：跟资源管理器一致的手感。
    /// 落在文件夹上 → DragMove；拖到窗口外（即将导出）→ DragCopy；其它 → 普通箭头。
    /// </summary>
    // 拖拽时每次 PointerMoved 都 new Cursor 会逐帧漏原生光标句柄（CA2000 实测），这里缓存三份
    private Cursor? _curArrow, _curDragMove, _curDragCopy;

    private Cursor DragCursor(StandardCursorType type)
    {
        switch (type)
        {
            case StandardCursorType.DragMove: return _curDragMove ??= new Cursor(type);
            case StandardCursorType.DragCopy: return _curDragCopy ??= new Cursor(type);
            default: return _curArrow ??= new Cursor(StandardCursorType.Arrow);
        }
    }

    private void SetDragCursor(bool overFolder, bool insideWindow)
    {
        var type = overFolder ? StandardCursorType.DragMove
                 : insideWindow ? StandardCursorType.Arrow
                 : StandardCursorType.DragCopy;
        try
        {
            var cursor = DragCursor(type);
            if (!ReferenceEquals(Cursor, cursor)) Cursor = cursor;
        }
        catch { }
    }

    private void ResetDragCursor()
    {
        try
        {
            var cursor = DragCursor(StandardCursorType.Arrow);
            if (!ReferenceEquals(Cursor, cursor)) Cursor = cursor;
        }
        catch { }
    }

    private void BeginRowDrag(IPointer pointer)
    {
        if (Vm == null) return;
        var sel = SelectedEntries();
        if (_pressEntry != null && sel.Contains(_pressEntry)) _rowDragItems = sel;
        else if (_pressEntry != null) _rowDragItems = new List<PosixEntry> { _pressEntry };
        else _rowDragItems = sel;
        if (_rowDragItems.Count == 0) return;

        _rowDrag = true;
        _dragTarget = null;
        _pendingToggle = null;
        pointer.Capture(Files);
        Vm.IsDragging = true;

        var start = Files.TranslatePoint(_pressPos, this) ?? new Point(0, 0);
        ShowDragGhost($"{_rowDragItems.Count} 项 · 拖到文件夹上移动，拖出窗口导出", start);
        Vm.DragHint = $"拖动 {_rowDragItems.Count} 项 · 文件夹上松手=移动，窗口外松手=导出";
    }

    private void UpdateRowDrag(Point pos)
    {
        if (Vm == null) return;
        var hit = HitTestEntry(pos);
        var target = hit is { IsDir: true } ? hit : null;
        if (!ReferenceEquals(target, _dragTarget))
        {
            _dragTarget = target;
            SetHighlight(target);
        }
        Vm.DragHint = target != null
            ? $"松开即移动到「{target.DisplayName}」"
            : "拖到文件夹上可移动 · 拖出窗口则导出到桌面/资源管理器";

        // 光标反馈：落在文件夹上=DragMove，窗口外=DragCopy（导出），其它=普通箭头
        var wp0 = Files.TranslatePoint(pos, this) ?? new Point(0, 0);
        var inside0 = wp0.X >= 0 && wp0.Y >= 0 && wp0.X <= Bounds.Width && wp0.Y <= Bounds.Height;
        SetDragCursor(target != null, inside0);

        // 跟随光标的提示：在窗口内说明"移动/导出"，出了窗口就只剩"导出到资源管理器"
        var wp = Files.TranslatePoint(pos, this) ?? new Point(0, 0);
        var inside = wp.X >= 0 && wp.Y >= 0 && wp.X <= Bounds.Width && wp.Y <= Bounds.Height;
        ShowDragGhost(
            target != null ? $"松开移动到「{target.DisplayName}」"
            : inside ? $"{_rowDragItems.Count} 项 · 拖到文件夹上移动，拖出窗口导出"
            : $"{_rowDragItems.Count} 项 · 松开即导出到桌面/资源管理器",
            wp);
    }

    private async Task FinishRowDragAsync(PointerEventArgs e)
    {
        if (Vm == null) return;
        HideDragGhost();
        ResetDragCursor();
        SetTreeHighlight(null);
        var items = _rowDragItems;
        _rowDragItems = new List<PosixEntry>();
        var target = _dragTarget;
        _dragTarget = null;
        SetHighlight(null);

        // 指针是否还在窗口里
        var wp = e.GetPosition(this);
        var inside = wp.X >= 0 && wp.Y >= 0 && wp.X <= Bounds.Width && wp.Y <= Bounds.Height;

        try
        {
            if (inside)
            {
                if (target != null)
                {
                    Vm.DragHint = $"正在移动 {items.Count} 项…";
                    await Vm.MoveAsync(items.Select(i => i.FullPath).ToArray(), target.FullPath);
                }
                else
                {
                    Vm.StatusText = "已取消拖动（没有落在文件夹上）";
                }
            }
            else
            {
                var dir = ShellFolderAtCursor() ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                Vm.DragHint = $"正在导出到 {dir} …";
                await Vm.ExportToAsync(items, dir);
            }
        }
        catch (Exception ex)
        {
            Vm.StatusText = "拖动失败：" + ex.Message;
        }
        finally
        {
            Vm.IsDragging = false;
        }
    }

    // ---------- 找出鼠标下面是哪个文件夹（资源管理器窗口） ----------

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private static string? ShellFolderAtCursor()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;   // 下面全是 Win32 / COM（CA1416）
            if (!GetCursorPos(out var pt)) return null;
            var root = GetAncestor(WindowFromPoint(pt), 2);   // GA_ROOT
            if (root == IntPtr.Zero) return null;

            var t = Type.GetTypeFromProgID("Shell.Application");
            if (t == null) return null;
            dynamic shell = Activator.CreateInstance(t)!;
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    if ((IntPtr)w.HWND == root)
                    {
                        string path = (string)w.Document.Folder.Self.Path;
                        if (Directory.Exists(path)) return path;
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private PosixEntry? HitTestEntry(Point p)
    {
        var c = Files.InputHitTest(p) as Control;
        while (c != null)
        {
            if (c is ListBoxItem li) return li.DataContext as PosixEntry;
            c = c.Parent as Control;
        }
        return null;
    }


    // ================= 模块管理 =================

    private ModuleInfo? ModuleOf(object? sender) => (sender as Control)?.DataContext as ModuleInfo;

    private void OnModulesRefresh(object? sender, RoutedEventArgs e)
    {
        if (Vm != null) _ = Vm.LoadModulesAsync();
    }

    private async void OnModuleToggle(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var m = ModuleOf(sender);
        if (m == null) return;
        await Vm.ToggleModuleAsync(m);
    }

    private async void OnModuleUninstall(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var m = ModuleOf(sender);
        if (m == null) return;
        var ok = await ConfirmDialog.ShowAsync(this, "卸载模块",
            $"确定卸载「{m.Name}」吗？\n\n卸载标记会在重启后生效。");
        if (!ok) return;
        await Vm.UninstallModuleAsync(m);
    }

    private async void OnModuleExtract(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var m = ModuleOf(sender);
        if (m == null) return;

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "提取模块 - " + m.Name,
                SuggestedFileName = m.Id + ".zip",
                DefaultExtension = "zip",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("ZIP 压缩包") { Patterns = new[] { "*.zip" } }
                }
            });
            if (file == null) return;

            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                Vm.StatusText = "请选择一个本地磁盘位置保存";
                return;
            }

            await Vm.ExportModuleAsync(m, path!);
        }
        catch (Exception ex)
        {
            Vm.StatusText = "提取失败：" + ex.Message;
        }
    }

    private async void OnModuleAction(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var m = ModuleOf(sender);
        if (m == null) return;
        if (!m.HasAction) { Vm.StatusText = "该模块没有 action.sh"; return; }

        Vm.StatusText = "正在执行 action.sh …";
        var output = await Vm.RunModuleActionAsync(m);
        await ConfirmDialog.ShowAsync(this, "执行结果 · " + m.Name,
            string.IsNullOrWhiteSpace(output) ? "（脚本没有输出）" : Trim(output, 3000));
    }

    // ================= 拖拽刷入模块 =================

    private void OnModulesDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnModulesDrop(object? sender, DragEventArgs e)
    {
        if (Vm == null) return;
        var files = e.Data.GetFiles();
        if (files == null) return;

        var paths = files.Select(f => f.TryGetLocalPath())
                         .Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToList();
        if (paths.Count == 0) return;
        e.Handled = true;

        foreach (var path in paths)
        {
            if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await ConfirmDialog.ShowAsync(this, "格式不支持",
                    $"{Path.GetFileName(path)}\n\n只能刷入 .zip 格式的模块包。");
                continue;
            }

            var ok = await ConfirmDialog.ShowAsync(this, "刷入模块",
                $"确定要刷入下面这个模块吗？\n\n{Path.GetFileName(path)}\n\n" +
                "刷入过程中部分模块需要按音量键或电源键，请在弹出窗口里操作。");
            if (!ok) continue;

            // 关掉窗口 = 取消这次安装；ShowAsync 会等 job 真正结束才返回，
            // 所以下面的 foreach 不会出现"上一个还在装、下一个已经开始"。
            await ModuleInstallWindow.ShowAsync(this, Path.GetFileName(path),
                async (log, ct) => { if (Vm != null) await Vm.InstallModuleZipAsync(path, log, ct); },
                async code => { if (Vm != null) await Vm.SendKeyAsync(code); });
        }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "\n…";
    private static string FirstLine(string s)
    {
        var i = s.IndexOf('\n');
        return i < 0 ? s : s[..i];
    }
}
