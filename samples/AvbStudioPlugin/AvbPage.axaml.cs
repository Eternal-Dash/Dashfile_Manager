using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DashFile.PluginApi;
using DashFile.PluginApi.Controls;
using AvbStudioPlugin.Avb;

namespace AvbStudioPlugin;

public sealed class ChildItem
{
    public string Path { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public string Partition { get; init; } = "";
    public string Kind { get; init; } = "";
    public string SizeText { get; init; } = "";
    public AvbInfo Info { get; init; } = null!;
}

public sealed class BatchItem
{
    public string Path { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public string Partition { get; init; } = "";
    public string Side { get; init; } = "";
    public string Rollback { get; init; } = "0";
    public string Algorithm { get; init; } = "";
    public AvbInfo Info { get; init; } = null!;
}

public sealed class ScanItem
{
    public string Path { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public string Partition { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Algorithm { get; init; } = "";
    public string Rollback { get; init; } = "0";
    public string SizeText { get; init; } = "";
    public AvbInfo Info { get; init; } = null!;
}

public sealed partial class AvbPage : UserControl
{
    private readonly IPluginHost _host;
    private readonly ObservableCollection<ChildItem> _children = new();
    private readonly ObservableCollection<BatchItem> _batch = new();
    private readonly ObservableCollection<ScanItem> _scan = new();

    private AvbInfo? _info;
    private AvbInfo? _refData;
    private bool _busy;
    private bool _scrollResetPending;

    // 概览
    // 引擎状态不再画在页面里：通过 _host.SetStatus(...) 推到宿主插件页顶部
    private ScrollViewer _pageScroll = null!;
    private SegmentedTabs _nav = null!;
    private StackPanel _overviewPage = null!, _signPage = null!, _vbmetaPage = null!, _rebuildPage = null!, _batchPage = null!, _scanPage = null!;

    // 签名
    private TextBox _srcBox = null!, _customKeyBox = null!, _rollbackBox = null!, _partitionBox = null!, _outBox = null!, _signLog = null!;
    private ComboBox _keyMode = null!, _algoBox = null!;
    private CheckBox _hashtreeBox = null!;
    private TextBlock _signStatus = null!, _infoTitle = null!, _infoDetails = null!;
    private Grid _customKeyRow = null!;
    private Button _signBtn = null!;

    // vbmeta
    private TextBox _refBox = null!, _vbOut = null!, _vbRollback = null!, _chainKeyBox = null!, _vbLog = null!;
    private ComboBox _vbAlgo = null!;
    private TextBlock _refInfoText = null!;
    private ListBox _childrenList = null!;

    // 整树重建
    private TextBox _rbInBox = null!, _rbOutBox = null!, _rbBackupBox = null!, _rbPartitions = null!, _rbKeyBox = null!, _rbLog = null!;
    private ComboBox _rbMode = null!, _rbKeyMode = null!;
    private TextBlock _rbModeHint = null!, _rbStatus = null!;
    private CheckBox _rbRegenSalt = null!, _rbVerifyOnly = null!;
    private Grid _rbCustomKeyRow = null!;
    private Button _rbBtn = null!;

    // 批处理
    private TextBox _batchDirBox = null!, _batchOutDir = null!, _batchKeyBox = null!, _batchLog = null!;
    private TextBlock _batchSummary = null!, _batchStatus = null!;
    private ListBox _batchList = null!;
    private Button _batchBtn = null!;
    private CheckBox _unifyRollback = null!, _stripAb = null!, _batchUseAosp = null!;
    private Grid _batchCustomKeyRow = null!;

    // 扫描
    private TextBox _scanDirBox = null!, _scanFilter = null!;
    private TextBlock _scanStatus = null!, _scanTotal = null!, _scanSigned = null!, _scanPlain = null!;
    private ListBox _scanList = null!;
    private Button _exportBtn = null!;

    public AvbPage() : this(null!) { }

    public AvbPage(IPluginHost host)
    {
        _host = host;
        try
        {
            InitializeComponent();
            BindControls();
            Wire();
            _childrenList.ItemsSource = _children;
            _batchList.ItemsSource = _batch;
            _scanList.ItemsSource = _scan;
            _pageScroll.LayoutUpdated += (_, _) =>
            {
                if (!_scrollResetPending) return;
                _scrollResetPending = false;
                _pageScroll.Offset = new Vector(0, 0);
            };
            _host.SetStatus("就绪 · 纯 C#");
            SwitchPage(0);
            AppendLog(_signLog, "AVB 工作台已就绪：内置 AOSP testkey，签名只在副本上执行。");
        }
        catch
        {
            throw;
        }
    }

    private T C<T>(string name) where T : Control
        => this.FindControl<T>(name) ?? throw new InvalidOperationException("XAML 缺少控件：" + name);

    private void BindControls()
    {
        _pageScroll = C<ScrollViewer>("PageScroll");
        _nav = C<SegmentedTabs>("Nav");
        _overviewPage = C<StackPanel>("OverviewPage");
        _signPage = C<StackPanel>("SignPage");
        _vbmetaPage = C<StackPanel>("VbmetaPage");
        _rebuildPage = C<StackPanel>("RebuildPage");
        _batchPage = C<StackPanel>("BatchPage");
        _scanPage = C<StackPanel>("ScanPage");

        _srcBox = C<TextBox>("SrcBox");
        _customKeyBox = C<TextBox>("CustomKeyBox");
        _rollbackBox = C<TextBox>("RollbackBox");
        _partitionBox = C<TextBox>("PartitionBox");
        _outBox = C<TextBox>("OutBox");
        _signLog = C<TextBox>("SignLog");
        _keyMode = C<ComboBox>("KeyMode");
        _algoBox = C<ComboBox>("AlgoBox");
        _hashtreeBox = C<CheckBox>("HashtreeBox");
        _signStatus = C<TextBlock>("SignStatus");
        _infoTitle = C<TextBlock>("InfoTitle");
        _infoDetails = C<TextBlock>("InfoDetails");
        _customKeyRow = C<Grid>("CustomKeyRow");
        _signBtn = C<Button>("SignBtn");

        _refBox = C<TextBox>("RefBox");
        _vbOut = C<TextBox>("VbOut");
        _vbRollback = C<TextBox>("VbRollback");
        _chainKeyBox = C<TextBox>("ChainKeyBox");
        _vbLog = C<TextBox>("VbLog");
        _vbAlgo = C<ComboBox>("VbAlgo");
        _refInfoText = C<TextBlock>("RefInfo");
        _childrenList = C<ListBox>("ChildrenList");

        _rbInBox = C<TextBox>("RbInBox");
        _rbOutBox = C<TextBox>("RbOutBox");
        _rbBackupBox = C<TextBox>("RbBackupBox");
        _rbPartitions = C<TextBox>("RbPartitions");
        _rbKeyBox = C<TextBox>("RbKeyBox");
        _rbLog = C<TextBox>("RbLog");
        _rbMode = C<ComboBox>("RbMode");
        _rbKeyMode = C<ComboBox>("RbKeyMode");
        _rbModeHint = C<TextBlock>("RbModeHint");
        _rbStatus = C<TextBlock>("RbStatus");
        _rbRegenSalt = C<CheckBox>("RbRegenSalt");
        _rbVerifyOnly = C<CheckBox>("RbVerifyOnly");
        _rbCustomKeyRow = C<Grid>("RbCustomKeyRow");
        _rbBtn = C<Button>("RbBtn");

        _batchDirBox = C<TextBox>("BatchDirBox");
        _batchOutDir = C<TextBox>("BatchOutDir");
        _batchKeyBox = C<TextBox>("BatchKeyBox");
        _batchLog = C<TextBox>("BatchLog");
        _batchSummary = C<TextBlock>("BatchSummary");
        _batchStatus = C<TextBlock>("BatchStatus");
        _batchList = C<ListBox>("BatchList");
        _batchBtn = C<Button>("BatchBtn");
        _unifyRollback = C<CheckBox>("UnifyRollback");
        _stripAb = C<CheckBox>("StripAb");
        _batchUseAosp = C<CheckBox>("BatchUseAosp");
        _batchCustomKeyRow = C<Grid>("BatchCustomKeyRow");

        _scanDirBox = C<TextBox>("ScanDirBox");
        _scanFilter = C<TextBox>("ScanFilter");
        _scanStatus = C<TextBlock>("ScanStatus");
        _scanTotal = C<TextBlock>("ScanTotal");
        _scanSigned = C<TextBlock>("ScanSigned");
        _scanPlain = C<TextBlock>("ScanPlain");
        _scanList = C<ListBox>("ScanList");
        _exportBtn = C<Button>("ExportBtn");
    }

    private void Wire()
    {
        _batchUseAosp.IsCheckedChanged += (_, _) =>
            _batchCustomKeyRow.IsVisible = _batchUseAosp.IsChecked != true;
        _batchCustomKeyRow.IsVisible = false;
        _rbKeyMode.SelectionChanged += (_, _) =>
            _rbCustomKeyRow.IsVisible = _rbKeyMode.SelectedIndex == 1;
        _rbCustomKeyRow.IsVisible = false;
        UpdateRbModeHint();
    }

    // ------------------------------------------------------------------ 导航

    private void OnNavSelectionChanged(object? sender, int index) => SwitchPage(index);

    private void OnQuickSign(object? sender, RoutedEventArgs e) => SwitchPage(1);
    private void OnQuickVbmeta(object? sender, RoutedEventArgs e) => SwitchPage(2);
    private void OnQuickBatch(object? sender, RoutedEventArgs e) => SwitchPage(4);
    private void OnQuickScan(object? sender, RoutedEventArgs e) => SwitchPage(5);

    private bool _switching;

    private void SwitchPage(int index)
    {
        if (index < 0 || index > 5) index = 0;

        // 滑块自己会滑过去；这里只管页面显隐，并且避免"设置 SelectedIndex → 回调 → 再设置"的抖动
        if (!_switching && _nav != null && _nav.SelectedIndex != index)
        {
            _switching = true;
            try { _nav.SelectedIndex = index; }
            finally { _switching = false; }
        }

        var pages = new[] { _overviewPage, _signPage, _vbmetaPage, _rebuildPage, _batchPage, _scanPage };
        for (var i = 0; i < pages.Length; i++)
        {
            var on = i == index;
            if (pages[i].IsVisible != on) pages[i].IsVisible = on;
            if (on) AnimateIn(pages[i]);
        }
        _scrollResetPending = true;
        _pageScroll.Offset = new Vector(0, 0);
        Dispatcher.UIThread.Post(() =>
        {
            _scrollResetPending = true;
            _pageScroll.Offset = new Vector(0, 0);
        }, DispatcherPriority.Loaded);
    }

    private static void AnimateIn(Control page)
    {
        page.Opacity = 0;
        var move = new TranslateTransform(0, 8);
        move.Transitions = new Transitions
        {
            new DoubleTransition { Property = TranslateTransform.YProperty, Duration = TimeSpan.FromMilliseconds(180) },
        };
        page.RenderTransform = move;
        page.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) },
        };
        Dispatcher.UIThread.Post(() =>
        {
            page.Opacity = 1;
            move.Y = 0;
        }, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------ 通用

    private static string Algo(ComboBox box)
        => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "SHA256_RSA4096";

    private void KeyModeChanged()
    {
        if (_customKeyRow == null || _customKeyBox == null) return;
        var custom = _keyMode.SelectedIndex == 1;
        _customKeyRow.IsVisible = custom;
        if (!custom && string.IsNullOrWhiteSpace(_customKeyBox.Text))
            _customKeyBox.Text = "";
        UpdateEngineState();
    }

    private void OnKeyModeChanged(object? sender, SelectionChangedEventArgs e) => KeyModeChanged();

    private string AospKey(string algorithm)
    {
        var bits = AospKeys.KeyBits(algorithm);
        var path = Path.Combine(_host.DataDirectory, "aosp_" + bits + ".pem");
        var pem = AospKeys.Pem(algorithm);
        if (!File.Exists(path) || File.ReadAllText(path) != pem)
            File.WriteAllText(path, pem, Encoding.ASCII);
        return path;
    }

    private string? PickKey(string algorithm)
    {
        if (_keyMode.SelectedIndex == 0) return AospKey(algorithm);
        var path = _customKeyBox.Text?.Trim() ?? "";
        return File.Exists(path) ? path : null;
    }

    private void UpdateEngineState()
    {
        var custom = _keyMode.SelectedIndex == 1;
        _host.SetStatus(custom ? "自定义私钥" : "就绪 · AOSP");
    }

    private void AppendLog(TextBox box, string text)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        var old = box.Text ?? "";
        var lines = old.Length == 0 ? new List<string>() : old.Split('\n').ToList();
        lines.Add(line);
        if (lines.Count > 120) lines.RemoveRange(0, lines.Count - 120);
        box.Text = string.Join("\n", lines);
        box.CaretIndex = box.Text.Length;
    }

    private void Toast(string message) => _host.Ui.Toast(message);

    // ------------------------------------------------------------------ 签名

    private async void OnBrowseSource(object? sender, RoutedEventArgs e)
    {
        var path = await _host.Ui.OpenFileAsync("选择 AVB 镜像", "*.img", "*.bin", "*.*");
        if (path == null) return;
        _srcBox.Text = path;
        ParseSource();
    }

    private void OnParseSource(object? sender, RoutedEventArgs e) => ParseSource();

    private void ParseSource()
    {
        var path = _srcBox.Text?.Trim() ?? "";
        if (!File.Exists(path))
        {
            _signStatus.Text = "文件不存在";
            return;
        }

        var info = AvbNative.Inspect(path);
        _info = info;
        RenderInfo(info);
        if (!info.Parsed)
        {
            _signStatus.Text = "解析失败：" + info.Error;
            return;
        }

        if (!info.Unsigned)
        {
            SetAlgo(_algoBox, info.Algorithm);
            if (string.IsNullOrWhiteSpace(_partitionBox.Text)) _partitionBox.Text = info.PartitionName;
            if (string.IsNullOrWhiteSpace(_rollbackBox.Text)) _rollbackBox.Text = info.RollbackIndex.ToString();
        }
        else if (string.IsNullOrWhiteSpace(_partitionBox.Text))
        {
            _partitionBox.Text = info.PartitionName;
        }

        if (string.IsNullOrWhiteSpace(_outBox.Text) ||
            _outBox.Text.Contains("_signed", StringComparison.OrdinalIgnoreCase) == false)
        {
            var dir = Path.GetDirectoryName(path) ?? "";
            _outBox.Text = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + "_signed.img");
        }
        _hashtreeBox.IsVisible = info.Unsigned;
        _hashtreeBox.IsChecked = info.Unsigned && info.PartitionName is "system" or "vendor" or "product" or "system_ext" or "odm";
        _signStatus.Text = info.Unsigned ? "未签名镜像 · 可创建 hash / hashtree footer" : "解析完成 · 可重签";
        AppendLog(_signLog, info.Unsigned ? "未检测到 AVB 签名，将创建新的 hash footer。" : "已解析 AVB 信息。");
    }

    private void RenderInfo(AvbInfo info)
    {
        if (!info.Parsed)
        {
            _infoTitle.Text = "解析失败";
            _infoDetails.Text = info.Error;
            return;
        }
        if (info.Unsigned)
        {
            _infoTitle.Text = "未签名镜像";
            _infoDetails.Text = $"文件大小：{FormatSize(info.FileSize)}\n分区名（推测）：{info.PartitionName}\n签名：无\n可直接创建 hash footer。";
            return;
        }

        var kind = info.IsFooter ? (info.IsHashtree ? "Hashtree Footer 分区" : "Hash Footer 分区") : "VBMeta";
        _infoTitle.Text = $"{kind} · {info.PartitionName} · {info.Algorithm}";
        var sb = new StringBuilder();
        sb.AppendLine($"文件大小：{FormatSize(info.FileSize)}");
        if (info.IsFooter) sb.AppendLine($"原始镜像：{FormatSize(info.OriginalImageSize)}");
        sb.AppendLine($"算法：{info.Algorithm}");
        sb.AppendLine($"回滚值：{info.RollbackIndex}（位置 {info.RollbackLocation}）");
        sb.AppendLine($"签名：{info.VerifyNote}");
        if (info.Salt.Length > 0) sb.AppendLine($"Salt：{Convert.ToHexString(info.Salt)[..Math.Min(32, info.Salt.Length * 2)]}…");
        foreach (var d in info.Descriptors)
            sb.AppendLine($"描述符：{d.Kind} {d.Partition} {d.Detail}");
        foreach (var p in info.Props) sb.AppendLine($"属性：{p.Key} = {p.Value}");
        _infoDetails.Text = sb.ToString().TrimEnd();
    }

    private async void OnBrowseOutput(object? sender, RoutedEventArgs e)
    {
        var path = await _host.Ui.SaveFileAsync("选择输出镜像", "signed.img", "*.img");
        if (path != null) _outBox.Text = path;
    }

    private async void OnBrowseKey(object? sender, RoutedEventArgs e)
    {
        var path = await _host.Ui.OpenFileAsync("选择 RSA 私钥", "*.pem", "*.key", "*.*");
        if (path != null) _customKeyBox.Text = path;
    }

    private async void OnSign(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var source = _srcBox.Text?.Trim() ?? "";
        if (!File.Exists(source)) { Toast("先选择源镜像"); return; }
        if (_info == null) ParseSource();
        if (_info == null || !_info.Parsed) { Toast("请先解析镜像"); return; }

        var algorithm = Algo(_algoBox);
        var key = PickKey(algorithm);
        if (key == null) { Toast("请选择有效的 RSA 私钥"); return; }
        var output = _outBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(output)) { Toast("请指定输出路径"); return; }
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
        { Toast("输出不能覆盖源镜像"); return; }

        var rollback = _info.RollbackIndex;
        if (ulong.TryParse(_rollbackBox.Text?.Trim(), out var r)) rollback = r;
        var partition = _partitionBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(partition)) partition = _info.PartitionName;

        _busy = true;
        _signBtn.IsEnabled = false;
        _signStatus.Text = "签名中…";
        AppendLog(_signLog, $"签名 {Path.GetFileName(source)} → {Path.GetFileName(output)} ({algorithm})");
        try
        {
            var result = await Task.Run(() => AvbNative.SignImage(source, output, new SignOptions
            {
                KeyPem = key,
                Algorithm = algorithm,
                PartitionName = partition ?? "",
                RollbackIndex = rollback,
                RollbackLocation = _info.RollbackLocation,
                UseHashtree = _info.Unsigned && _hashtreeBox.IsChecked == true,
                Props = _info.Props,
            }));
            if (result.Ok)
            {
                _signStatus.Text = result.Message;
                AppendLog(_signLog, "完成：" + output);
                Toast("签名完成");
                if (result.Info != null)
                {
                    _info = result.Info;
                    RenderInfo(result.Info);
                }
            }
            else
            {
                _signStatus.Text = "失败：" + result.Message;
                AppendLog(_signLog, "失败：" + result.Message);
                Toast("签名失败：" + result.Message);
            }
        }
        catch (Exception ex)
        {
            _signStatus.Text = "失败：" + ex.Message;
            AppendLog(_signLog, "异常：" + ex.Message);
        }
        finally
        {
            _busy = false;
            _signBtn.IsEnabled = true;
        }
    }

    private void OnClearSignLog(object? sender, RoutedEventArgs e) => _signLog.Text = "";

    // ------------------------------------------------------------------ VBMeta

    private async void OnBrowseRef(object? sender, RoutedEventArgs e)
    {
        var path = await _host.Ui.OpenFileAsync("选择参考 vbmeta", "*.img", "*.bin", "*.*");
        if (path == null) return;
        _refBox.Text = path;
        ParseRef();
    }

    private void ParseRef()
    {
        var path = _refBox.Text?.Trim() ?? "";
        if (!File.Exists(path)) { _refInfoText.Text = "文件不存在"; return; }
        var info = AvbNative.Inspect(path);
        _refData = info;
        if (!info.Parsed) { _refInfoText.Text = "解析失败：" + info.Error; return; }
        SetAlgo(_vbAlgo, info.Algorithm);
        _vbRollback.Text = info.RollbackIndex.ToString();
        _refInfoText.Text = $"{info.Algorithm} · 回滚 {info.RollbackIndex} · {info.Descriptors.Count} 个描述符";
    }

    private async void OnAddChildren(object? sender, RoutedEventArgs e)
    {
        var paths = await _host.Ui.OpenFilesAsync("添加已签名的子分区镜像", "*.img", "*.bin", "*.*");
        if (paths.Count == 0) return;
        foreach (var path in paths)
        {
            if (_children.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            var info = AvbNative.Inspect(path);
            if (!info.Parsed || info.Descriptors.All(d => d.Tag is not (AvbNative.TagHash or AvbNative.TagHashtree)))
            {
                Toast("跳过没有 Hash/Hashtree 描述符的文件：" + Path.GetFileName(path));
                continue;
            }
            _children.Add(new ChildItem
            {
                Path = path,
                Partition = info.PartitionName,
                Kind = info.IsHashtree ? "Hashtree" : "Hash",
                SizeText = FormatSize(info.FileSize),
                Info = info,
            });
        }
        UpdateVbState();
    }

    private void OnClearChildren(object? sender, RoutedEventArgs e)
    {
        _children.Clear();
        UpdateVbState();
    }

    private void UpdateVbState()
    {
        if (string.IsNullOrWhiteSpace(_vbOut.Text) && _children.Count > 0)
        {
            var dir = Path.GetDirectoryName(_children[0].Path) ?? "";
            _vbOut.Text = Path.Combine(dir, "vbmeta.img");
        }
    }

    private async void OnBrowseVbOut(object? sender, RoutedEventArgs e)
    {
        var path = await _host.Ui.SaveFileAsync("保存 vbmeta", "vbmeta.img", "*.img");
        if (path != null) _vbOut.Text = path;
    }

    private async void OnBuildVbmeta(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_children.Count == 0) { Toast("先添加已签名的子分区"); return; }
        var output = _vbOut.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(output)) { Toast("请指定输出路径"); return; }
        var algorithm = Algo(_vbAlgo);
        var key = PickKey(algorithm);
        if (key == null) { Toast("请选择有效的 RSA 私钥"); return; }
        ulong rollback = 0;
        ulong.TryParse(_vbRollback.Text?.Trim(), out rollback);

        _busy = true;
        try
        {
            var descriptorSets = _children
                .SelectMany(c => c.Info.Descriptors
                    .Where(d => d.Tag is AvbNative.TagHash or AvbNative.TagHashtree)
                    .Select(d => d.Raw))
                .ToList();

            if (_refData != null && _refData.Parsed && !string.IsNullOrWhiteSpace(_chainKeyBox.Text) &&
                File.Exists(_chainKeyBox.Text.Trim()))
            {
                var pub = File.ReadAllBytes(_chainKeyBox.Text.Trim());
                foreach (var d in _refData.Descriptors.Where(d => d.Tag == AvbNative.TagChain))
                    descriptorSets.Add(AvbNative.BuildChainDescriptor(d.Partition, d.RollbackLocation, pub));
            }

            var bytes = await Task.Run(() => AvbNative.BuildVbmetaImage(descriptorSets, algorithm, key, rollback, 0));
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
            await File.WriteAllBytesAsync(output, bytes);
            AppendLog(_vbLog, $"生成 {output}（{FormatSize(bytes.Length)}）");
            Toast("VBMeta 构建完成");
            _refData = AvbNative.Inspect(output);
        }
        catch (Exception ex)
        {
            AppendLog(_vbLog, "失败：" + ex.Message);
            Toast("构建失败：" + ex.Message);
        }
        finally { _busy = false; }
    }

    private void OnClearVbLog(object? sender, RoutedEventArgs e) => _vbLog.Text = "";

    // ------------------------------------------------------------------ 整树重建

    private void UpdateRbModeHint()
    {
        if (_rbModeHint == null) return;
        _rbModeHint.Text = _rbMode.SelectedIndex == 1
            ? "链式模式：每个分区独立签名，保留原 vbmeta.img，适合只改 boot / vbmeta_system 等分区。"
            : "非链式模式：普通分区只写无签名 hash footer，由新的 vbmeta.img 统一签名（需要输入目录里有 vbmeta.img）；链式分区仍单独重签。";
    }

    private void OnRbModeChanged(object? sender, SelectionChangedEventArgs e) => UpdateRbModeHint();

    private void OnRbKeyModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_rbCustomKeyRow != null) _rbCustomKeyRow.IsVisible = _rbKeyMode.SelectedIndex == 1;
    }

    private async void OnBrowseRbIn(object? sender, RoutedEventArgs e)
    {
        var path = await _host.Ui.PickFolderAsync("选择输入目录");
        if (path == null) return;
        _rbInBox.Text = path;
        if (string.IsNullOrWhiteSpace(_rbOutBox.Text)) _rbOutBox.Text = path.TrimEnd('\\') + "_out";
        if (string.IsNullOrWhiteSpace(_rbBackupBox.Text)) _rbBackupBox.Text = Path.Combine(path, "backup");
    }

    private async void OnBrowseRbOut(object? sender, RoutedEventArgs e)
    {
        var path = await _host.Ui.PickFolderAsync("选择输出目录");
        if (path != null) _rbOutBox.Text = path;
    }

    private async void OnBrowseRbBackup(object? sender, RoutedEventArgs e)
    {
        var path = await _host.Ui.PickFolderAsync("选择备份目录");
        if (path != null) _rbBackupBox.Text = path;
    }

    private async void OnBrowseRbKey(object? sender, RoutedEventArgs e)
    {
        var path = await _host.Ui.OpenFileAsync("选择 RSA 私钥", "*.pem", "*.key", "*.*");
        if (path != null) _rbKeyBox.Text = path;
    }

    private async void OnRunRebuild(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var input = _rbInBox.Text?.Trim() ?? "";
        var output = _rbOutBox.Text?.Trim() ?? "";
        if (!Directory.Exists(input)) { Toast("请选择有效的输入目录"); return; }
        if (string.IsNullOrWhiteSpace(output)) { Toast("请指定输出目录"); return; }

        var verifyOnly = _rbVerifyOnly.IsChecked == true;
        var useAosp = _rbKeyMode.SelectedIndex == 0;
        var key = _rbKeyBox.Text?.Trim() ?? "";
        if (!verifyOnly && !useAosp && !File.Exists(key)) { Toast("请选择有效的私钥"); return; }

        _busy = true;
        _rbBtn.IsEnabled = false;
        _rbStatus.Text = verifyOnly ? "校验中…" : (_rbMode.SelectedIndex == 1 ? "链式重建中…" : "重建中…");
        AppendLog(_rbLog, "开始 " + (verifyOnly ? "校验" : _rbMode.SelectedIndex == 1 ? "链式重建" : "非链式重建"));
        try
        {
            var options = new RebuildOptions
            {
                InputDir = input,
                OutputDir = output,
                BackupDir = _rbBackupBox.Text?.Trim() ?? "",
                ChainedMode = _rbMode.SelectedIndex == 1,
                RegenerateSalt = _rbRegenSalt.IsChecked == true,
                VerifyOnly = verifyOnly,
                Partitions = _rbPartitions.Text?.Trim() ?? "",
                UseAospKey = useAosp,
                KeyPem = useAosp ? null : key,
                Log = line => Dispatcher.UIThread.Post(() => AppendLog(_rbLog, line)),
            };
            var result = await Task.Run(() => AvbNative.RebuildAll(options));
            _rbStatus.Text = result.Message + $"（成功 {result.Rebuilt} / 失败 {result.Failed} / 跳过 {result.Skipped}）";
            AppendLog(_rbLog, result.Message);
            Toast(result.Ok ? result.Message : "重建失败：" + result.Message);
        }
        catch (Exception ex)
        {
            _rbStatus.Text = "失败：" + ex.Message;
            AppendLog(_rbLog, "异常：" + ex.Message);
        }
        finally
        {
            _busy = false;
            _rbBtn.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ 批处理

    private async void OnBrowseBatchDir(object? sender, RoutedEventArgs e)
    {
        var path = await _host.Ui.PickFolderAsync("选择 A/B 分区目录");
        if (path == null) return;
        _batchDirBox.Text = path;
        if (string.IsNullOrWhiteSpace(_batchOutDir.Text))
            _batchOutDir.Text = Path.Combine(path, "signed");
        ScanBatch();
    }

    private async void OnBrowseBatchOut(object? sender, RoutedEventArgs e)
    {
        var path = await _host.Ui.PickFolderAsync("选择输出目录");
        if (path != null) _batchOutDir.Text = path;
    }

    private async void OnBrowseBatchKey(object? sender, RoutedEventArgs e)
    {
        var path = await _host.Ui.OpenFileAsync("选择 RSA 私钥", "*.pem", "*.key", "*.*");
        if (path != null) _batchKeyBox.Text = path;
    }

    private void OnScanBatch(object? sender, RoutedEventArgs e) => ScanBatch();

    private async void ScanBatch()
    {
        var dir = _batchDirBox.Text?.Trim() ?? "";
        if (!Directory.Exists(dir)) { Toast("请选择目录"); return; }
        _busy = true;
        _batchBtn.IsEnabled = false;
        _batchStatus.Text = "扫描中…";
        try
        {
            var files = await Task.Run(() =>
            {
                try { return Directory.GetFiles(dir, "*.img", SearchOption.AllDirectories).Take(400).ToArray(); }
                catch { return Array.Empty<string>(); }
            });
            _batch.Clear();
            foreach (var f in files)
            {
                var info = AvbNative.Inspect(f);
                if (!info.Parsed || !info.IsFooter) continue;
                var name = Path.GetFileNameWithoutExtension(f);
                var side = name.EndsWith("_b", StringComparison.OrdinalIgnoreCase) ? "B"
                    : name.EndsWith("_a", StringComparison.OrdinalIgnoreCase) ? "A" : "—";
                _batch.Add(new BatchItem
                {
                    Path = f,
                    Partition = info.PartitionName,
                    Side = side,
                    Rollback = info.RollbackIndex.ToString(),
                    Algorithm = info.Algorithm,
                    Info = info,
                });
            }
            var max = _batch.Select(b => ulong.TryParse(b.Rollback, out var r) ? r : 0).DefaultIfEmpty(0UL).Max();
            _batchSummary.Text = $"{_batch.Count} 个分区 · 最高回滚值 {max}";
            _batchStatus.Text = "扫描完成";
        }
        finally
        {
            _busy = false;
            _batchBtn.IsEnabled = _batch.Count > 0;
        }
    }

    private async void OnRunBatch(object? sender, RoutedEventArgs e)
    {
        if (_busy || _batch.Count == 0) return;
        var outDir = _batchOutDir.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(outDir)) { Toast("请指定输出目录"); return; }
        Directory.CreateDirectory(outDir);

        var unify = _unifyRollback.IsChecked == true;
        var strip = _stripAb.IsChecked == true;
        var maxRollback = _batch.Select(b => ulong.TryParse(b.Rollback, out var r) ? r : 0).DefaultIfEmpty(0UL).Max();
        _busy = true;
        _batchBtn.IsEnabled = false;
        var ok = 0;
        var fail = 0;
        foreach (var item in _batch.ToList())
        {
            _batchStatus.Text = $"签名 {item.Name}…";
            var algorithm = string.IsNullOrWhiteSpace(item.Algorithm) ? "SHA256_RSA4096" : item.Algorithm;
            string? key;
            if (_batchUseAosp.IsChecked == true)
            {
                if (!AospKeys.Supports(algorithm)) { AppendLog(_batchLog, "跳过（AOSP 无此算法）：" + item.Name); fail++; continue; }
                key = AospKey(algorithm);
            }
            else key = File.Exists(_batchKeyBox.Text?.Trim() ?? "") ? _batchKeyBox.Text!.Trim() : null;
            if (key == null) { AppendLog(_batchLog, "缺少私钥，停止"); break; }

            var name = Path.GetFileNameWithoutExtension(item.Name);
            if (strip && name.Length > 2 && (name.EndsWith("_a") || name.EndsWith("_b"))) name = name[..^2];
            var output = Path.Combine(outDir, name + "_signed.img");
            var result = await Task.Run(() => AvbNative.SignImage(item.Path, output, new SignOptions
            {
                KeyPem = key,
                Algorithm = algorithm,
                PartitionName = item.Partition,
                RollbackIndex = unify ? maxRollback : (ulong.TryParse(item.Rollback, out var r) ? r : 0),
            }));
            if (result.Ok) { ok++; AppendLog(_batchLog, "✓ " + name + " → " + output); }
            else { fail++; AppendLog(_batchLog, "✗ " + name + "：" + result.Message); }
        }
        _batchStatus.Text = $"完成：{ok} 成功 / {fail} 失败";
        _busy = false;
        _batchBtn.IsEnabled = true;
    }

    // ------------------------------------------------------------------ 扫描

    private async void OnBrowseScanDir(object? sender, RoutedEventArgs e)
    {
        var path = await _host.Ui.PickFolderAsync("选择扫描目录");
        if (path == null) return;
        _scanDirBox.Text = path;
        ScanImages();
    }

    private void OnScanImages(object? sender, RoutedEventArgs e) => ScanImages();

    private async void ScanImages()
    {
        var dir = _scanDirBox.Text?.Trim() ?? "";
        if (!Directory.Exists(dir)) { Toast("请选择目录"); return; }
        var pattern = string.IsNullOrWhiteSpace(_scanFilter.Text) ? "*.img" : _scanFilter.Text.Trim();
        _scanStatus.Text = "扫描中…";
        _scan.Clear();
        try
        {
            var files = await Task.Run(() =>
            {
                try { return Directory.GetFiles(dir, pattern, SearchOption.AllDirectories).Take(500).ToArray(); }
                catch { return Array.Empty<string>(); }
            });
            foreach (var f in files)
            {
                var info = AvbNative.Inspect(f);
                if (!info.Parsed || info.Unsigned) continue;
                _scan.Add(new ScanItem
                {
                    Path = f,
                    Partition = info.PartitionName,
                    Kind = info.IsFooter ? (info.IsHashtree ? "Hashtree" : "Hash") : "VBMeta",
                    Algorithm = info.Algorithm,
                    Rollback = info.RollbackIndex.ToString(),
                    SizeText = FormatSize(info.FileSize),
                    Info = info,
                });
            }
            _scanTotal.Text = files.Length.ToString();
            _scanSigned.Text = _scan.Count.ToString();
            _scanPlain.Text = Math.Max(0, files.Length - _scan.Count).ToString();
            _scanStatus.Text = $"完成：{files.Length} 个文件中 {_scan.Count} 个带 AVB";
            _exportBtn.IsEnabled = _scan.Count > 0;
        }
        catch (Exception ex)
        {
            _scanStatus.Text = "扫描失败：" + ex.Message;
        }
    }

    private async void OnExportScan(object? sender, RoutedEventArgs e)
    {
        if (_scan.Count == 0) return;
        var path = await _host.Ui.SaveFileAsync("导出扫描报告", "avb_scan_report.txt", "*.txt");
        if (path == null) return;
        var sb = new StringBuilder();
        sb.AppendLine("AVB 工作台扫描报告");
        sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("目录：" + _scanDirBox.Text);
        sb.AppendLine(new string('-', 78));
        sb.AppendLine("文件\t分区\t类型\t算法\t回滚值\t大小(字节)\t签名\t路径");
        foreach (var h in _scan)
            sb.AppendLine($"{h.Name}\t{h.Partition}\t{h.Kind}\t{h.Algorithm}\t{h.Rollback}\t{h.Info.FileSize}\t{h.Info.VerifyNote}\t{h.Path}");
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
        Toast("报告已导出");
    }

    // ------------------------------------------------------------------ 工具

    private static void SetAlgo(ComboBox box, string algorithm)
    {
        for (var i = 0; i < box.ItemCount; i++)
        {
            if (box.Items[i] is ComboBoxItem ci &&
                string.Equals(ci.Content?.ToString(), algorithm, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        var mb = bytes / 1024.0 / 1024.0;
        return mb >= 1024 ? $"{mb / 1024:0.00} GB" : $"{mb:0.0} MB";
    }
}
