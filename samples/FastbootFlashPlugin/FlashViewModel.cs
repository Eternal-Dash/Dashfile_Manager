using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashFile.PluginApi;

namespace FastbootFlashPlugin;

public partial class PartitionRow : ObservableObject
{
    public Partition Partition { get; }
    public string Name => Partition.Name;
    public string Detail => Partition.SizeText + " · " + Partition.Kind;
    [ObservableProperty] private bool _selected;
    public PartitionRow(Partition partition) => Partition = partition;
}
public partial class FlashViewModel : ObservableObject
{
    private readonly IPluginHost _host;
    private readonly FlashEngine _engine;
    private CancellationTokenSource? _cts;
    private Task? _running;
    private DeviceDetails? _details;
    private FlashPlan? _plan;
    private readonly List<PartitionRow> _catalog = new();
    public ObservableCollection<DeviceTarget> Devices { get; } = new();
    public ObservableCollection<PartitionRow> Partitions { get; } = new();
    public string[] Slots { get; } = { "current", "a", "b" };
    [ObservableProperty] private DeviceTarget? _selectedDevice;
    [ObservableProperty] private PartitionRow? _selectedPartition;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _targetPartition = "";
    [ObservableProperty] private string _slot = "current";
    [ObservableProperty] private string _imagePath = "";
    [ObservableProperty] private string _packagePath = "";
    [ObservableProperty] private string _outputFolder;
    [ObservableProperty] private bool _manualUnlock;
    [ObservableProperty] private bool _allowCritical;
    [ObservableProperty] private bool _zipBackup = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "连接设备后，点击检测设备。所有操作均绑定所选序列号。";
    [ObservableProperty] private string _deviceSummary = "尚未读取设备信息";
    [ObservableProperty] private string _planText = "先生成预览，核对目标、槽位与文件后再执行。";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = "就绪";
    [ObservableProperty] private int _tabIndex;
    public bool CanInteract => !IsBusy;
    public bool HasPlan => _plan != null && !IsBusy;
    public bool ImageTab => TabIndex == 0;
    public bool PackageTab => TabIndex == 1;
    public bool BackupTab => TabIndex == 2;
    public bool LogTab => TabIndex == 3;
    public string CatalogSummary => $"已枚举 {_catalog.Count} 个分区 · 已勾选 {_catalog.Count(p => p.Selected)} 个";
    public FlashViewModel(IPluginHost host, FlashEngine engine)
    {
        _host = host; _engine = engine; _outputFolder = Path.Combine(host.DataDirectory, "backups");
        engine.Log += AddLog;
        engine.Progress += (done, total, name) => host.Ui.RunOnUiThread(() => { ProgressValue = total == 0 ? 0 : done * 100d / total; ProgressText = $"{done}/{total} · {name}"; });
        PropertyChanged += (_, e) =>
        {
            if (new[] { nameof(TargetPartition), nameof(Slot), nameof(ImagePath), nameof(PackagePath), nameof(ManualUnlock), nameof(AllowCritical) }.Contains(e.PropertyName)) InvalidatePlan();
        };
    }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanInteract)); OnPropertyChanged(nameof(HasPlan)); }
    partial void OnTabIndexChanged(int value) { foreach (var n in new[] { nameof(ImageTab), nameof(PackageTab), nameof(BackupTab), nameof(LogTab) }) OnPropertyChanged(n); }
    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnSelectedPartitionChanged(PartitionRow? value) { if (value != null) TargetPartition = value.Name; }
    partial void OnSelectedDeviceChanged(DeviceTarget? value)
    {
        _details = null; _catalog.Clear(); Partitions.Clear(); DeviceSummary = "请点击读取分区，确认当前模式与机型。"; InvalidatePlan(); OnPropertyChanged(nameof(CatalogSummary));
    }
    private void ApplyFilter() { Partitions.Clear(); foreach (var row in _catalog.Where(p => p.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase))) Partitions.Add(row); }
    private void InvalidatePlan() { _plan = null; PlanText = "先生成预览，核对目标、槽位与文件后再执行。"; OnPropertyChanged(nameof(HasPlan)); }
    private DeviceTarget Target() => SelectedDevice ?? throw new InvalidOperationException("请先检测并选择设备。");
    private DeviceDetails Details() => _details ?? throw new InvalidOperationException("请先读取设备信息与分区表。");
    private void AddLog(string text) => _host.Ui.RunOnUiThread(() =>
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}\n";
        LogText += line;
        if (LogText.Length > 60000) LogText = LogText[^50000..];
    });
    private Task Run(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return Task.CompletedTask;
        IsBusy = true; _cts = new(); ProgressValue = 0; ProgressText = "处理中…";
        return _running = RunCore(action, _cts.Token);
    }
    private async Task RunCore(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        try { await action(ct); Status = "操作完成。"; ProgressText = "已完成"; }
        catch (OperationCanceledException) { Status = "已停止。已完成的写入不会回滚；回读失败结果保留为不完整备份。"; AddLog(Status); }
        catch (Exception ex) { Status = ex.Message; ProgressText = "任务未完成"; AddLog("错误：" + ex.Message); _host.Log.Error(ex.Message); }
        finally
        {
            IsBusy = false; _cts?.Dispose(); _cts = null;
            try { Directory.CreateDirectory(Path.Combine(_host.DataDirectory, "logs")); await File.WriteAllTextAsync(Path.Combine(_host.DataDirectory, "logs", "latest.log"), LogText, CancellationToken.None); } catch { }
        }
    }
    [RelayCommand] private Task DetectAsync() => Run(async ct =>
    {
        InvalidatePlan(); var devices = await _engine.DiscoverAsync(ct); Devices.Clear(); foreach (var d in devices) Devices.Add(d);
        SelectedDevice = Devices.FirstOrDefault();
        if (devices.Count == 0) throw new InvalidOperationException("未发现设备。请检查 USB、驱动及 Fastboot 模式；Android 回读需 ADB 授权和 Root。");
    });
    [RelayCommand] private Task InspectAsync() => Run(async ct =>
    {
        InvalidatePlan(); _details = await _engine.InspectAsync(Target(), ct);
        var d = _details; DeviceSummary = $"{d.Product}  ·  {(d.Target.Transport == Transport.AdbRoot ? "ADB Root · 仅回读" : d.Userspace ? "fastbootd" : "Bootloader Fastboot")}  ·  槽位 {d.Slot}  ·  解锁：{(d.Unlocked == true ? "是" : d.Unlocked == false ? "否" : "未报告")}";
        _catalog.Clear(); foreach (var p in d.Partitions) { var row = new PartitionRow(p); row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CatalogSummary)); _catalog.Add(row); }
        ApplyFilter(); OnPropertyChanged(nameof(CatalogSummary));
        if (_catalog.Count == 0) AddLog("设备未返回可枚举分区。可手填分区名生成单镜像计划；不支持完整备份。");
    });
    [RelayCommand] private async Task PickImageAsync() { if (!IsBusy && await _host.Ui.OpenFileAsync("选择分区镜像", "*.img", "*.bin") is { } p) ImagePath = p; }
    [RelayCommand] private async Task PickPackageAsync() { if (!IsBusy && await _host.Ui.OpenFileAsync("选择固件 / DashFile 备份包", "*.zip") is { } p) PackagePath = p; }
    [RelayCommand] private async Task PickPackageFolderAsync() { if (!IsBusy && await _host.Ui.PickFolderAsync("选择镜像目录 / 备份目录") is { } p) PackagePath = p; }
    [RelayCommand] private async Task PickOutputAsync() { if (!IsBusy && await _host.Ui.PickFolderAsync("选择回读输出目录") is { } p) OutputFolder = p; }
    private void ShowPlan(FlashPlan plan)
    {
        _plan = plan;
        PlanText = $"设备：{plan.Target.Serial}  /  {plan.Product}\n{plan.Notes}\n\n" + string.Join("\n\n", plan.Steps.Select((s, i) => $"{i + 1}. {s.Partition} ← {Path.GetFileName(s.Image.Path)}\n展开大小 {Partition.FormatBytes(s.Image.ExpandedSize)} · SHA-256 {s.Image.Sha256}"));
        OnPropertyChanged(nameof(HasPlan)); AddLog("计划已生成，共 " + plan.Steps.Count + " 项。尚未写入设备。");
    }
    [RelayCommand] private Task PreviewImageAsync() => Run(async ct => { InvalidatePlan(); ShowPlan(await _engine.PlanImagesAsync(Details(), new[] { (TargetPartition.Trim(), ImagePath.Trim()) }, Slot, ManualUnlock, AllowCritical, ct)); });
    [RelayCommand] private Task PreviewPackageAsync() => Run(async ct =>
    {
        InvalidatePlan(); var d = Details(); var path = PackagePath.Trim();
        if (path.EndsWith(".dfbackup.zip", StringComparison.OrdinalIgnoreCase)) path = await FlashEngine.ExtractBackupAsync(path, Path.Combine(_host.DataDirectory, "staging"), ct);
        ShowPlan(Directory.Exists(path) ? await _engine.PlanFolderAsync(d, path, Slot, ManualUnlock, AllowCritical, ct) : await _engine.PlanUpdateAsync(d, path, ManualUnlock, AllowCritical, ct));
    });
    [RelayCommand] private Task ExecuteAsync() => Run(async ct =>
    {
        var plan = _plan ?? throw new InvalidOperationException("请先生成预览。");
        var phrase = "FLASH " + plan.Target.Serial;
        var answer = await _host.Ui.PromptAsync("确认写入设备", "将覆盖目标分区，可能丢失数据或导致设备无法启动。请核对计划，并输入：" + phrase, "", ct);
        if (answer != phrase) throw new OperationCanceledException();
        if (!await _host.Ui.ConfirmAsync("最终核对", PlanText + "\n\n执行后请勿拔线。整包 update 是一个原生任务，停止将在其结束后生效。", "开始刷写", "取消", ct)) throw new OperationCanceledException();
        _plan = null; OnPropertyChanged(nameof(HasPlan)); await _engine.ExecuteAsync(plan); AddLog("写入完成；设备保持当前模式，请自行确认后重启。");
    });
    private Task Backup(bool full) => Run(async ct =>
    {
        var details = Details(); var parts = full ? _catalog.Select(p => p.Partition).ToArray() : _catalog.Where(p => p.Selected).Select(p => p.Partition).ToArray();
        if (parts.Length == 0) throw new InvalidOperationException("请勾选要回读的分区。");
        if (!await _host.Ui.ConfirmAsync(full ? "回读全部已枚举分区" : "回读所选分区", $"设备 {details.Target.Serial}\n{parts.Length} 个分区 · 约 {Partition.FormatBytes(parts.Sum(p => p.Size))}\n输出：{OutputFolder}\n\nFastboot 需要设备支持 fetch；ADB 需要 Root。全部指设备返回的分区，不等于厂商完整刷机包。Android 运行中分区可能变化，重要备份建议在支持 Root 的 Recovery 中进行。任一失败即停止，未完成包不可用于整包恢复。", "开始回读", "取消", ct)) throw new OperationCanceledException();
        var folder = await _engine.BackupAsync(details, parts, OutputFolder, full, ZipBackup, ct); AddLog("已验证备份：" + folder);
    });
    [RelayCommand] private Task BackupSelectedAsync() => Backup(false);
    [RelayCommand] private Task BackupAllAsync() => Backup(true);
    [RelayCommand] private void SelectAll() { if (!IsBusy) foreach (var row in Partitions) row.Selected = true; }
    [RelayCommand] private void SelectNone() { if (!IsBusy) foreach (var row in _catalog) row.Selected = false; }
    [RelayCommand] private Task RebootAsync(string mode) => Run(async ct =>
    {
        var target = Target(); if (!await _host.Ui.ConfirmAsync("切换设备模式", $"设备：{target.Serial}\n目标：{mode}\n请先结束其他设备操作。", "确认切换", "取消", ct)) throw new OperationCanceledException();
        await _engine.RebootAsync(target, mode, ct); _details = null; InvalidatePlan(); _catalog.Clear(); Partitions.Clear(); DeviceSummary = "模式切换后请重新检测并读取分区。";
    });
    [RelayCommand] private void Stop() { _engine.RequestStop(); if (!_engine.IsWriting) _cts?.Cancel(); Status = _engine.IsWriting ? "已请求停止后续任务，正在等待当前写入自然结束，请勿拔线。" : "正在停止…"; }
    [RelayCommand] private Task ExportLogAsync() => Run(async ct =>
    {
        if (await _host.Ui.SaveFileAsync("导出任务日志", "fastboot-task.log", "*.log") is not { } path) throw new OperationCanceledException();
        await File.WriteAllTextAsync(path, LogText, ct);
    });
    public async Task StopAsync() { Stop(); if (_running != null) await _running; }
}
