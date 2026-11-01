using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashFile.PluginApi;

namespace FastbootFlashPlugin;

public partial class PartitionRow : ObservableObject
{
    public Partition Partition { get; }
    public string Name => Partition.Name;
    public string SizeText => Partition.FormatBytes(Partition.Size);
    public string KindText => Partition.Kind;
    public string Detail => SizeText + " · " + KindText;

    [ObservableProperty] private bool _selected;
    [ObservableProperty] private string _imagePath = "";

    public string ImageLabel => string.IsNullOrEmpty(ImagePath) ? "选择文件" : Path.GetFileName(ImagePath);
    public bool HasImage => !string.IsNullOrEmpty(ImagePath);

    partial void OnImagePathChanged(string value)
    {
        OnPropertyChanged(nameof(ImageLabel));
        OnPropertyChanged(nameof(HasImage));
    }

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
    private bool _updatingRows;
    private readonly object _logGate = new();
    private readonly StringBuilder _pendingLog = new();
    private bool _logPosted;

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
    [ObservableProperty] private string _commandText = "";
    [ObservableProperty] private bool _manualUnlock;
    [ObservableProperty] private bool _allowCritical;
    [ObservableProperty] private bool _zipBackup = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isLogOpen;
    [ObservableProperty] private int _deviceModeIndex;
    public string[] DeviceModes { get; } = { "自动选择单台", "手动指定设备" };
    public bool AutoDetect => DeviceModeIndex == 0;
    public string TargetDisplay => SelectedDevice?.Display ?? "检测设备后自动选择唯一目标";
    public bool DeviceSelectionEnabled => CanInteract && !AutoDetect;
    public bool CatalogEmpty => _catalog.Count == 0;
    public bool ListEmpty => Partitions.Count == 0;
    public string EmptyTitle => CatalogEmpty ? "从设备分区开始" : "没有匹配的分区";
    public string EmptyDescription => CatalogEmpty ? "检测设备并读取分区后，可逐行选择镜像，也可勾选后回读。ADB Root 设备仅支持回读。" : "请尝试缩短关键词；筛选不会清除隐藏行的勾选。";
    public bool HasDetails => _details != null;
    public bool CanUseDevice => CanInteract && SelectedDevice != null;
    public bool CanUsePartitions => CanInteract && HasDetails;
    public bool CanWriteDevice => CanUsePartitions && _details?.Target.Transport == Transport.Fastboot && _details.Unlocked != false;
    [ObservableProperty] private bool _isBasicsOpen = true;
    [ObservableProperty] private bool _isPackageOpen = false;
    [ObservableProperty] private bool _isImageOpen = false;
    [ObservableProperty] private bool _isBackupOpen = false;
    [ObservableProperty] private bool _isAdvancedOpen = false;
    [ObservableProperty] private bool _isPlanOpen;
    [ObservableProperty] private string _status = "接上设备后点「检测设备」，再点「读取分区」。所有操作都绑定所选序列号。";
    [ObservableProperty] private string _deviceSummary = "尚未读取设备信息";
    [ObservableProperty] private string _planText = "先生成预览，核对目标、槽位与文件后再执行。";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = "就绪";

    public bool CanInteract => !IsBusy;
    public bool HasPlan => _plan != null && !IsBusy;
    public int SelectedCount => _catalog.Count(p => p.Selected);
    public string CatalogSummary => _catalog.Count == 0
        ? "读取分区后在此勾选"
        : "已枚举 " + _catalog.Count + " 个 · 已选 " + SelectedCount + " 个" + (Filter.Length > 0 ? " · 当前显示 " + Partitions.Count + " 个（含隐藏勾选 " + (SelectedCount - Partitions.Count(p => p.Selected)) + " 个）" : "");

    public FlashViewModel(IPluginHost host, FlashEngine engine)
    {
        _host = host; _engine = engine;
        _outputFolder = Path.Combine(host.DataDirectory, "backups");
        engine.Log += AddLog;
        engine.Progress += (done, total, name) => host.Ui.RunOnUiThread(() =>
        {
            ProgressValue = total == 0 ? 0 : done * 100d / total;
            ProgressText = total == 0 ? name : done + "/" + total + " · " + name;
        });
        PropertyChanged += (_, e) =>
        {
            if (new[] { nameof(TargetPartition), nameof(Slot), nameof(ImagePath), nameof(PackagePath), nameof(ManualUnlock), nameof(AllowCritical) }.Contains(e.PropertyName))
                InvalidatePlan();
        };
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInteract)); OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(DeviceSelectionEnabled)); OnPropertyChanged(nameof(CanUseDevice)); OnPropertyChanged(nameof(CanUsePartitions)); OnPropertyChanged(nameof(CanWriteDevice));
    }
    partial void OnDeviceModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(AutoDetect)); OnPropertyChanged(nameof(DeviceSelectionEnabled));
        if (AutoDetect) SelectedDevice = Devices.Count == 1 ? Devices[0] : null;
    }
    private void NotifyCatalog()
    {
        OnPropertyChanged(nameof(CatalogSummary)); OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(CatalogEmpty)); OnPropertyChanged(nameof(ListEmpty)); OnPropertyChanged(nameof(EmptyTitle)); OnPropertyChanged(nameof(EmptyDescription));
        OnPropertyChanged(nameof(HasDetails)); OnPropertyChanged(nameof(CanUseDevice)); OnPropertyChanged(nameof(CanUsePartitions)); OnPropertyChanged(nameof(CanWriteDevice));
    }
    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updatingRows) return;
        if (e.PropertyName is nameof(PartitionRow.Selected) or nameof(PartitionRow.ImagePath)) { InvalidatePlan(); NotifyCatalog(); }
    }
    private void ClearCatalog()
    {
        foreach (var row in _catalog) row.PropertyChanged -= OnRowChanged;
        _catalog.Clear(); Partitions.Clear(); NotifyCatalog();
    }
    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnSelectedPartitionChanged(PartitionRow? value) { if (value != null) TargetPartition = value.Name; }
    partial void OnSelectedDeviceChanged(DeviceTarget? value)
    {
        OnPropertyChanged(nameof(TargetDisplay));
        _details = null; ClearCatalog();
        DeviceSummary = "请点击「读取分区」，确认当前模式与机型。";
        InvalidatePlan();
        OnPropertyChanged(nameof(CatalogSummary));
        OnPropertyChanged(nameof(SelectedCount));
    }

    private void ApplyFilter()
    {
        Partitions.Clear();
        foreach (var row in _catalog.Where(p => p.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase))) Partitions.Add(row);
        OnPropertyChanged(nameof(CatalogSummary));
        OnPropertyChanged(nameof(SelectedCount));
        NotifyCatalog();
    }

    private void InvalidatePlan() { _plan = null; PlanText = "先生成预览，核对目标、槽位与文件后再执行。"; OnPropertyChanged(nameof(HasPlan)); }
    private DeviceTarget Target() => SelectedDevice ?? throw new InvalidOperationException("请先检测并选择设备。");
    private DeviceDetails Details() => _details ?? throw new InvalidOperationException("请先读取设备信息与分区表。");

    private void AddLog(string text)
    {
        lock (_logGate)
        {
            _pendingLog.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").AppendLine(text);
            if (_pendingLog.Length > 60000) _pendingLog.Remove(0, _pendingLog.Length - 50000);
            if (_logPosted) return;
            _logPosted = true;
        }
        _host.Ui.RunOnUiThread(FlushLog);
    }
    private void FlushLog()
    {
        string pending;
        lock (_logGate) { pending = _pendingLog.ToString(); _pendingLog.Clear(); _logPosted = false; }
        if (pending.Length == 0) return;
        LogText += pending;
        if (LogText.Length > 60000) LogText = LogText[^50000..];
    }

    private Task Run(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return Task.CompletedTask;
        IsBusy = true; _cts = new(); ProgressValue = 0; ProgressText = "处理中…";
        return _running = RunCore(action, _cts.Token);
    }

    private async Task RunCore(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        try { await action(ct); Status = "操作完成。"; ProgressText = "已完成"; }
        catch (OperationCanceledException) { Status = "已停止。已完成的写入不会回滚；回读失败的结果保留为不完整备份。"; AddLog(Status); }
        catch (Exception ex) { Status = ex.Message; IsLogOpen = true; ProgressText = "任务未完成"; AddLog("错误：" + ex.Message); _host.Log.Error(ex.Message); }
        finally
        {
            FlushLog();
            IsBusy = false; _cts?.Dispose(); _cts = null;
            try
            {
                Directory.CreateDirectory(Path.Combine(_host.DataDirectory, "logs"));
                await File.WriteAllTextAsync(Path.Combine(_host.DataDirectory, "logs", "latest.log"), LogText, CancellationToken.None);
            }
            catch { }
        }
    }

    [RelayCommand] private Task DetectAsync() => Run(async ct =>
    {
        InvalidatePlan();
        _details = null; ClearCatalog();
        var previous = SelectedDevice;
        var devices = await _engine.DiscoverAsync(ct);
        Devices.Clear();
        foreach (var d in devices) Devices.Add(d);
        SelectedDevice = AutoDetect ? (Devices.Count == 1 ? Devices[0] : null) : Devices.FirstOrDefault(d => d == previous);
        if (devices.Count > 1 && AutoDetect) throw new InvalidOperationException("检测到多台设备。请切换为手动指定设备，再明确选择目标序列号。");
        if (devices.Count == 0) throw new InvalidOperationException("未发现设备。请检查 USB、驱动及 Fastboot 模式；ADB 回读需要授权与 Root。");
    });

    [RelayCommand] private Task InspectAsync() => Run(async ct =>
    {
        InvalidatePlan();
        _details = await _engine.InspectAsync(Target(), ct);
        var d = _details;
        var mode = d.Target.Transport == Transport.AdbRoot ? "ADB Root · 仅回读" : (d.Userspace ? "fastbootd" : "Bootloader Fastboot");
        var unlocked = d.Unlocked == true ? "是" : d.Unlocked == false ? "否" : "未报告";
        DeviceSummary = d.Product + "  ·  " + mode + "  ·  槽位 " + d.Slot + "  ·  解锁：" + unlocked;
        ClearCatalog();
        foreach (var p in d.Partitions)
        {
            var row = new PartitionRow(p);
            row.PropertyChanged += OnRowChanged;
            _catalog.Add(row);
        }
        ApplyFilter();
        if (_catalog.Count == 0) AddLog("设备未返回可枚举分区。可手填分区名生成单镜像计划；不支持完整备份。");
    });

    [RelayCommand] private async Task PickImageAsync() { if (!IsBusy && await _host.Ui.OpenFileAsync("选择分区镜像", "*.img", "*.bin") is { } p) ImagePath = p; }
    [RelayCommand] private async Task PickPackageAsync() { if (!IsBusy && await _host.Ui.OpenFileAsync("选择固件 / DashFile 备份包", "*.zip") is { } p) PackagePath = p; }
    [RelayCommand] private async Task PickPackageFolderAsync() { if (!IsBusy && await _host.Ui.PickFolderAsync("选择镜像目录 / 备份目录") is { } p) PackagePath = p; }
    [RelayCommand] private async Task PickOutputAsync() { if (!IsBusy && await _host.Ui.PickFolderAsync("选择回读输出目录") is { } p) OutputFolder = p; }

    [RelayCommand] private async Task PickRowImageAsync(PartitionRow? row)
    {
        if (IsBusy || row == null) return;
        if (await _host.Ui.OpenFileAsync("为 " + row.Name + " 选择镜像", "*.img", "*.bin") is { } p)
        {
            row.ImagePath = p;
            row.Selected = true;
            TargetPartition = row.Name;
        }
    }

    private void ShowPlan(FlashPlan plan)
    {
        _plan = plan;
        IsPlanOpen = true;
        var steps = string.Join(Environment.NewLine + Environment.NewLine,
            plan.Steps.Select((s, i) => (i + 1) + ". " + s.Partition + " ← " + Path.GetFileName(s.Image.Path) +
                Environment.NewLine + "展开大小 " + Partition.FormatBytes(s.Image.ExpandedSize) + " · SHA-256 " + s.Image.Sha256));
        PlanText = "设备：" + plan.Target.Serial + "  /  " + plan.Product + Environment.NewLine + plan.Notes +
                   Environment.NewLine + Environment.NewLine + steps;
        OnPropertyChanged(nameof(HasPlan));
        AddLog("计划已生成，共 " + plan.Steps.Count + " 项。尚未写入设备。");
    }

    [RelayCommand] private Task PreviewImageAsync() => Run(async ct =>
    {
        InvalidatePlan();
        ShowPlan(await _engine.PlanImagesAsync(Details(), new[] { (TargetPartition.Trim(), ImagePath.Trim()) }, Slot, ManualUnlock, AllowCritical, ct));
    });

    [RelayCommand] private Task FlashSelectedAsync() => Run(async ct =>
    {
        var selected = _catalog.Where(p => p.Selected).ToArray();
        var missing = selected.Where(p => !p.HasImage).Select(p => p.Name).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException("以下选中分区尚未选择镜像：" + string.Join("、", missing));
        var picked = selected.Select(p => (p.Name, p.ImagePath)).ToList();
        if (picked.Count == 0) throw new InvalidOperationException("请先勾选分区，并在对应行选择镜像文件。");
        InvalidatePlan();
        ShowPlan(await _engine.PlanImagesAsync(Details(), picked, Slot, ManualUnlock, AllowCritical, ct));
    });

    [RelayCommand] private Task PreviewPackageAsync() => Run(async ct =>
    {
        InvalidatePlan();
        var d = Details();
        var path = PackagePath.Trim();
        if (path.EndsWith(".dfbackup.zip", StringComparison.OrdinalIgnoreCase))
            path = await FlashEngine.ExtractBackupAsync(path, Path.Combine(_host.DataDirectory, "staging"), ct);
        ShowPlan(Directory.Exists(path)
            ? await _engine.PlanFolderAsync(d, path, Slot, ManualUnlock, AllowCritical, ct)
            : await _engine.PlanUpdateAsync(d, path, ManualUnlock, AllowCritical, ct));
    });

    [RelayCommand] private Task ExecuteAsync() => Run(async ct =>
    {
        var plan = _plan ?? throw new InvalidOperationException("请先生成预览。");
        var phrase = "FLASH " + plan.Target.Serial;
        var answer = await _host.Ui.PromptAsync("确认写入设备", "将覆盖目标分区，可能丢失数据或导致设备无法启动。请核对计划，并输入：" + phrase, "", ct);
        if (answer != phrase) throw new OperationCanceledException();
        var confirm = await _host.Ui.ConfirmAsync("最终核对", PlanText + Environment.NewLine + Environment.NewLine +
            "执行后请勿拔线。整包 update 是一个原生任务，停止将在其结束后生效。", "开始刷写", "取消", ct);
        if (!confirm) throw new OperationCanceledException();
        _plan = null; OnPropertyChanged(nameof(HasPlan));
        await _engine.ExecuteAsync(plan);
        AddLog("写入完成；设备保持当前模式，请自行确认后重启。");
    });

    [RelayCommand] private Task EraseSelectedAsync() => Run(async ct =>
    {
        var details = Details();
        var parts = _catalog.Where(p => p.Selected).Select(p => p.Partition).ToArray();
        if (parts.Length == 0) throw new InvalidOperationException("请先勾选要擦除的分区。");
        var list = string.Join(Environment.NewLine, parts.Select(p => "  · " + p.Name));
        var phrase = "ERASE " + details.Target.Serial;
        if (await _host.Ui.PromptAsync("确认擦除目标", "擦除不会保留镜像，且不能撤销。请输入：" + phrase, "", ct) != phrase) throw new OperationCanceledException();
        var ok = await _host.Ui.ConfirmAsync("擦除分区",
            "设备 " + details.Target.Serial + Environment.NewLine +
            "将擦除 " + parts.Length + " 个分区的全部内容，不可恢复：" + Environment.NewLine + Environment.NewLine + list +
            Environment.NewLine + Environment.NewLine + "擦除后设备可能无法启动，请确认已准备好镜像。",
            "开始擦除", "取消", ct);
        if (!ok) throw new OperationCanceledException();
        InvalidatePlan();
        await _engine.EraseAsync(details, parts, AllowCritical, ct, ManualUnlock);
        AddLog("擦除任务结束。");
    });

    private Task Backup(bool full) => Run(async ct =>
    {
        var details = Details();
        var parts = full ? _catalog.Select(p => p.Partition).ToArray() : _catalog.Where(p => p.Selected).Select(p => p.Partition).ToArray();
        if (parts.Length == 0) throw new InvalidOperationException("请勾选要回读的分区。");
        var head = "设备 " + details.Target.Serial + Environment.NewLine + parts.Length + " 个分区 · 约 " +
                   Partition.FormatBytes(parts.Sum(p => p.Size)) + Environment.NewLine + "输出：" + OutputFolder +
                   Environment.NewLine + Environment.NewLine +
                   "Fastboot 需要设备支持 fetch；ADB 需要 Root。全部指设备返回的分区，不等于厂商完整刷机包。Android 运行中分区可能变化，重要备份建议在 Root Recovery 的未挂载状态进行。" +
                   "任一失败即停止，未完成包不可用于整包恢复。";
        if (!await _host.Ui.ConfirmAsync(full ? "回读全部已枚举分区" : "回读所选分区", head, "开始回读", "取消", ct))
            throw new OperationCanceledException();
        var folder = await _engine.BackupAsync(details, parts, OutputFolder, full, ZipBackup, ct);
        AddLog("已验证备份：" + folder);
    });

    [RelayCommand] private Task BackupSelectedAsync() => Backup(false);
    [RelayCommand] private Task BackupAllAsync() => Backup(true);

    [RelayCommand] private void SelectAll()
    {
        if (IsBusy) return;
        _updatingRows = true;
        try { foreach (var row in Partitions) row.Selected = true; }
        finally { _updatingRows = false; InvalidatePlan(); NotifyCatalog(); }
        OnPropertyChanged(nameof(CatalogSummary)); OnPropertyChanged(nameof(SelectedCount));
    }

    [RelayCommand] private void SelectNone()
    {
        if (IsBusy) return;
        _updatingRows = true;
        try { foreach (var row in _catalog) row.Selected = false; }
        finally { _updatingRows = false; InvalidatePlan(); NotifyCatalog(); }
        OnPropertyChanged(nameof(CatalogSummary)); OnPropertyChanged(nameof(SelectedCount));
    }

    [RelayCommand] private void ToggleLog() => IsLogOpen = !IsLogOpen;

    [RelayCommand] private Task RunCommandAsync() => Run(async ct =>
    {
        var target = Target();
        var cmd = CommandText.Trim();
        if (cmd.Length == 0) throw new InvalidDataException("请先输入查询命令，例如 getvar all。");
        InvalidatePlan();
        IsLogOpen = true;
        var ok = await _host.Ui.ConfirmAsync("执行 Fastboot 查询",
            "设备 " + target.Serial + Environment.NewLine + Environment.NewLine + "fastboot " + cmd +
            Environment.NewLine + Environment.NewLine + "仅支持 getvar / oem device-info 查询。写入、擦除、解锁及设备重选参数均不由此入口执行。",
            "执行", "取消", ct);
        if (!ok) throw new OperationCanceledException();
        var output = await _engine.RunCommandAsync(target, cmd, ct);
        AddLog(string.IsNullOrWhiteSpace(output) ? "命令已完成，无输出。" : output);
    });

    [RelayCommand] private Task RebootTo(string mode) => Run(async ct =>
    {
        var target = Target();
        var label = mode switch
        {
            "system" => "系统",
            "fastboot" => "Fastbootd",
            "bootloader" => "Fastboot（Bootloader）",
            "recovery" => "Recovery",
            "edl" => "9008 / EDL",
            _ => mode
        };
        var ok = await _host.Ui.ConfirmAsync("重启设备",
            "设备：" + target.Serial + Environment.NewLine + "目标：" + label +
            Environment.NewLine + Environment.NewLine + "请先结束其他设备操作。", "确认重启", "取消", ct);
        if (!ok) throw new OperationCanceledException();
        await _engine.RebootAsync(target, mode, ct);
        _details = null; InvalidatePlan(); ClearCatalog();
        DeviceSummary = "模式切换后请重新检测并读取分区。";
        AddLog("已发送重启指令：" + label);
    });

    [RelayCommand] private void Stop()
    {
        _engine.RequestStop();
        if (!_engine.IsWriting) _cts?.Cancel();
        Status = _engine.IsWriting ? "已请求停止后续任务，正在等待当前写入自然结束，请勿拔线。" : "正在停止…";
    }

    [RelayCommand] private Task ExportLogAsync() => Run(async ct =>
    {
        if (await _host.Ui.SaveFileAsync("导出任务日志", "fastboot-task.log", "*.log") is not { } path)
            throw new OperationCanceledException();
        await File.WriteAllTextAsync(path, LogText, ct);
    });

    public async Task StopAsync() { Stop(); if (_running != null) await _running; }
}
