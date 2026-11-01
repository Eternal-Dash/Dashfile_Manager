using System;
using System.Collections.Generic;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DashFile.Models;
using DashFile.PluginApi;
using DashFile.Services;
using DashFile.Services.Root;

namespace DashFile.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public FileTransferRegistry FileTransfers { get; } = new();
    private readonly AdbRunner _runner;
    private readonly AdbClient _client;
    private RemoteFileSystem? _fs;
    private CancellationTokenSource? _cts;
    private readonly PowerService _power;
    private ModuleService? _modules;
    private bool _connecting;

    public MainViewModel()
    {
        var adb = AdbLocator.Resolve();
        _runner = new AdbRunner(adb);
        _client = new AdbClient(_runner);
        _power = new PowerService(_runner);
        AdbPath = adb;
        FileTransfers.Status += message => Avalonia.Threading.Dispatcher.UIThread.Post(() => LogLine(message));
        FileTransfers.Operations.Started += operation => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var owner=(Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if(owner==null||operation.Snapshot.Finished)return;
            try{new DashFile.Views.TransferProgressWindow(operation).Show(owner);}
            catch(Exception ex){operation.Cancel();LogLine("传输窗口无法打开："+ex.Message);}
        });

        // 说明：设备检测统一走 StartWatchdog() 的 1.2s 轮询 + WM_DEVICECHANGE 加速。
        // （原来的 AdbDeviceTracker 从未被启动/订阅，已作为死代码删除）

        foreach (var t in ThemeOption.Presets()) Themes.Add(t);
        Styles.Add(new StyleOption { Key = "m3", Name = "标准", Hint = "Material You 风：12px 圆角、色调表面" });
        Styles.Add(new StyleOption { Key = "miui", Name = "MIUI", Hint = "MIUIx 风：18px 大圆角、更亮的面" });

        // 新版默认黑白主题：老配置迁移一次
        if (!AppSettings.Current.ThemeDefaultV2)
        {
            AppSettings.Current.AccentHex = "#FFFFFF";
            AppSettings.Current.ThemeDefaultV2 = true;
            AppSettings.Save();
        }

        if (AppSettings.Current.EnsureLightMaterialDefault()) AppSettings.Save();
        var saved = AppSettings.Current.AccentHex;
        // 玻璃风格已移除：老配置里的 "glass" 归位成标准风格，并写回设置
        var savedStyle = string.Equals(AppSettings.Current.StyleKey, "miui", StringComparison.OrdinalIgnoreCase)
            ? "miui" : "m3";
        if (!string.Equals(AppSettings.Current.StyleKey, savedStyle, StringComparison.Ordinal))
        {
            AppSettings.Current.StyleKey = savedStyle;
            AppSettings.Save();
        }
        AccentHex = saved;
        StyleKey = savedStyle;
        foreach (var t in Themes) t.IsSelected = string.Equals(t.Hex, saved, StringComparison.OrdinalIgnoreCase);
        foreach (var st in Styles) st.IsSelected = string.Equals(st.Key, savedStyle, StringComparison.OrdinalIgnoreCase);
        IsGlassEnabled = AppSettings.Current.GlassEnabled;
        IsMaterialEnabled = AppSettings.Current.MaterialEnabled;
        IsLightMode = string.Equals(AppSettings.Current.Appearance, "light", StringComparison.OrdinalIgnoreCase);
        ThemeService.ApplyAppearance(IsLightMode ? "light" : "dark", savedStyle, saved);

        // 调试用：DASHFILE_START_TAB=0~5 可直接以某个标签页启动（不影响正常使用）
        if (AppEnvironment.StartTab is { } startTab) SelectedTab = startTab;
        if (AppEnvironment.AppMode is { } appMode) AppMode = appMode;
        Perf($"start tab -> {SelectedTab} mode={AppMode} (env={AppEnvironment.StartTab?.ToString() ?? "null"})");

    }

    public string AdbPath { get; }

    public ObservableCollection<DeviceInfo> Devices { get; } = new();
    public BulkObservableCollection<PosixEntry> Entries { get; } = new();
    private List<PosixEntry> _entriesRaw = new();
    private string _sortColumn = "type";
    [ObservableProperty] private string _filterText = "";
    private bool _sortAsc = false;
    public ObservableCollection<string> LogLines { get; } = new();

    public BulkObservableCollection<TreeRow> TreeRows { get; } = new();

    /// <summary>树的行布局/当前项变了（视图据此移动高亮滑块，不重新绑定 ItemsSource）</summary>
    public event Action? TreeVisualChanged;
    private void NotifyTreeVisual() => TreeVisualChanged?.Invoke();
    public ObservableCollection<PathNode> Crumbs { get; } = new();
    [ObservableProperty] private bool _isPathEditing;
    private readonly Dictionary<string, DirNode> _nodes = new(StringComparer.Ordinal);
    private DirNode? _rootNode;



    [ObservableProperty] private DeviceInfo? _selectedDevice;
    [ObservableProperty] private PosixEntry? _selectedEntry;
    [ObservableProperty] private string _currentPath = "/";
    [ObservableProperty] private string _statusText = "准备就绪";
    [ObservableProperty] private string _hintText = "提示：双击文件夹进入 · 选中文件后可上传/下载/执行/改权限";
    [ObservableProperty] private string _rootBadge = "未连接";
    [ObservableProperty] private string _deviceSub = "请用数据线连接手机，并打开「开发者选项 → USB 调试」";
    [ObservableProperty] private bool _isRoot;
    /// <summary>当前 root 实现改完授权后是否需要重启（KSU 需要，MagiskSU 不需要）。</summary>
    public bool RootGrantNeedsReboot => _root?.GrantChangeNeedsReboot ?? true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _selectionInfo = "";
    // ---- 分页 ----
    [ObservableProperty] private int _selectedTab = 0;

    public bool IsDeviceTab => SelectedTab == 0;
    public bool IsFileTab => SelectedTab == 1;
    public bool IsModulesTab => SelectedTab == 2;
    public bool IsAppsTab => SelectedTab == 3;
    public bool IsPluginsTab => SelectedTab == 4;
    public bool IsSettingsTab => SelectedTab == 5;
    public bool IsNavigationTab => SelectedTab >= 6 && SelectedTab - 6 < NavigationPages.Count;
    public int NavigationIndex
    {
        get => DashFile.Services.Plugins.PluginNavigation.ToVisualIndex(SelectedTab, NavigationPages.Count);
        set => SelectedTab = DashFile.Services.Plugins.PluginNavigation.ToPageIndex(value, NavigationPages.Count);
    }
    public string RightStatusText => IsDeviceTab ? BatteryShort : StatusText;

    [ObservableProperty] private string _deviceModeText = "未连接";
    [ObservableProperty] private string _modeBadge = "";
    [ObservableProperty] private bool _modulesEmpty = true;
    [ObservableProperty] private string _modulesStatus = "";
    public ObservableCollection<ModuleInfo> Modules { get; } = new();

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsDeviceTab));
        OnPropertyChanged(nameof(IsFileTab));
        OnPropertyChanged(nameof(IsModulesTab));
        OnPropertyChanged(nameof(IsAppsTab));
        OnPropertyChanged(nameof(IsPluginsTab));
        OnPropertyChanged(nameof(IsSettingsTab));
        OnPropertyChanged(nameof(IsNavigationTab));
        OnPropertyChanged(nameof(NavigationIndex));
        if (IsNavigationTab) _ = CreateNavigationViewAsync();
        OnPropertyChanged(nameof(RightStatusText));
        if (value == 0) LoadDeviceInfoAsync(_lastSerial, "", "").Safe("读取设备信息", m => StatusText = m);
        if (value == 2) LoadModulesAsync().Safe("读取模块", m => StatusText = m);
        if (value == 3) LoadAppsAsync().Safe("读取应用", m => StatusText = m);
        if (value == 4) RefreshPlugins();
        OnPropertyChanged(nameof(ShowFooterSign));
    }

    /// <summary>文件页/设置页自带署名，其余页面用全局灰色署名兜底</summary>
    public bool ShowFooterSign => SelectedTab != 1 && SelectedTab != 5;

    [RelayCommand] private void ShowDeviceTab() => SelectedTab = 0;
    [RelayCommand] private void ShowFileTab() => SelectedTab = 1;
    [RelayCommand] private void ShowModulesTab() => SelectedTab = 2;
    [RelayCommand] private void ShowAppsTab() => SelectedTab = 3;
    [RelayCommand] private void ShowPluginsTab() => SelectedTab = 4;
    [RelayCommand] private void ShowSettingsTab() => SelectedTab = 5;

    /// <summary>手动重置 adb 连接（会重启 adb 服务；部分设备重置后需重新插拔）</summary>
    [RelayCommand]
    public async Task ResetConnectionAsync()
    {
        try
        {
            StatusText = "正在重置 adb 连接…";
            await _client.ResetServerAsync();
            await RefreshDevicesAsync();
            StatusText = "连接已重置（若仍无设备，请重新插拔一次数据线）";
        }
        catch (Exception ex) { StatusText = "重置失败：" + ex.Message; }
    }


    // ---- 快捷重启 ----
    [RelayCommand] private Task RebootSystemAsync() => RebootAsync(RebootTarget.System);
    [RelayCommand] private Task RebootBootloaderAsync() => RebootAsync(RebootTarget.Bootloader);
    [RelayCommand] private Task RebootFastbootdAsync() => RebootAsync(RebootTarget.Fastbootd);
    [RelayCommand] private Task RebootRecoveryAsync() => RebootAsync(RebootTarget.Recovery);
    [RelayCommand] private Task RebootEdlAsync() => RebootAsync(RebootTarget.Edl);

    public async Task RebootAsync(RebootTarget target)
    {
        var serial = SelectedDevice?.Serial;
        if (string.IsNullOrEmpty(serial)) serial = _lastSerial;
        if (string.IsNullOrEmpty(serial)) serial = _lastKnownSerial;
        if (string.IsNullOrEmpty(serial)) { StatusText = "没有连接设备"; return; }

        try
        {
            StatusText = "正在识别设备当前模式…";
            var mode = await _power.DetectModeAsync(serial);
            DeviceModeText = PowerService.ModeText(mode);
            if (mode == DeviceMode.None)
            {
                StatusText = "设备当前既不在 ADB 也不在 Fastboot，无法发送重启命令";
                Toast("设备当前不可控（可能在 Recovery 或正在启动），请稍后重试");
                return;
            }

            StatusText = $"当前 {PowerService.ModeText(mode)} → 正在重启到 {TargetText(target)}…";
            await _power.RebootAsync(serial, mode, target);
            StatusText = $"已发送重启命令：{TargetText(target)}";
            Toast(StatusText);

            // 之后跟着设备走一遍，实时报告它落到了哪个模式（不然用户完全不知道成功没有）
            TrackRebootAsync(serial, target).Safe("跟踪重启");
        }
        catch (Exception ex) { StatusText = "重启失败：" + ex.Message; }
    }

    /// <summary>
    /// 重启后的阶段跟踪：每 800ms 问一次设备现在是什么模式，
    /// 状态栏依次显示「等待重新枚举 → 已进入 Fastboot → 完成」，最多跟 20 秒。
    /// </summary>
    private async Task TrackRebootAsync(string serial, RebootTarget target)
    {
        var want = target switch
        {
            RebootTarget.System => DeviceMode.System,
            RebootTarget.Bootloader => DeviceMode.Fastboot,
            RebootTarget.Fastbootd => DeviceMode.Fastbootd,
            RebootTarget.Recovery => DeviceMode.Recovery,
            _ => DeviceMode.None,          // 9008 没法用 adb/fastboot 判断
        };

        var left = false;                  // 是否已经观察到"设备消失"（说明真的在重启）
        for (var i = 0; i < 25; i++)
        {
            await Task.Delay(800);
            DeviceMode mode;
            try { mode = await _power.DetectModeAsync(serial); }
            catch { continue; }

            if (mode == DeviceMode.None)
            {
                left = true;
                StatusText = "重启中… 等待设备重新枚举";
                continue;
            }

            var text = PowerService.ModeText(mode);
            DeviceModeText = text;

            if (mode == want)
            {
                StatusText = $"已重启到 {text}";
                Toast(StatusText);
                return;
            }

            StatusText = (left ? "重启中… " : "") + $"当前 {text}";
        }

        StatusText = "重启后没有检测到设备（可能仍在启动，可点「重新查找设备」）";
    }

    private static string TargetText(RebootTarget t) => t switch
    {
        RebootTarget.System => "系统",
        RebootTarget.Bootloader => "Fastboot",
        RebootTarget.Fastbootd => "Fastbootd",
        RebootTarget.Recovery => "Recovery",
        RebootTarget.Edl => "9008 / EDL",
        _ => "系统",
    };

    // ---- 模块管理 ----
    public async Task LoadModulesAsync()
    {
        if (_fs == null) { ModulesStatus = "未连接设备"; ModulesEmpty = true; return; }
        try
        {
            ModulesStatus = "正在读取模块…";
            _modules ??= new ModuleService(_fs, _runner, _root?.ModuleInstallCommand, _root?.ModuleUninstallCommand);
            var list = await _modules.ListAsync();
            Modules.Clear();
            foreach (var m in list) Modules.Add(m);
            ModulesEmpty = list.Count == 0;
            ModulesStatus = $"共 {list.Count} 个模块";
        }
        catch (Exception ex) { ModulesStatus = "读取失败：" + ex.Message; }
    }

    [RelayCommand] private Task RefreshModulesAsync() => LoadModulesAsync();

    public async Task<bool> InstallModuleZipAsync(string localZip, Action<string> onLog,
        CancellationToken ct = default)
    {
        if (_fs == null) { onLog("× 未连接设备"); return false; }

        // 安装前先在本地校验：坏包绝不能上传，
        //   否则 KernelSU 会“先删掉同名旧模块再解压”，解压失败就把旧模块搞没了。
        var problem = ValidateModuleZip(localZip);
        if (problem != null)
        {
            onLog("× 拒绝安装：" + problem);
            onLog("→ 已取消，设备上已有的模块没有任何改动");
            StatusText = "安装已取消：" + problem;
            return false;
        }

        _modules ??= new ModuleService(_fs, _runner, _root?.ModuleInstallCommand, _root?.ModuleUninstallCommand);
        try
        {
            var code = await _modules.InstallAsync(localZip, onLog, ct);
            await LoadModulesAsync();
            onLog("");
            onLog("→ 已刷新模块列表");
            return code == 0;
        }
        catch (Exception ex) { onLog("× 异常：" + ex.Message); return false; }
    }

    /// <summary>校验是不是一个能被 KernelSU 识别的模块包；返回 null 表示通过</summary>
    private static string? ValidateModuleZip(string path)
    {
        try
        {
            if (!File.Exists(path)) return "文件不存在";
            if (new FileInfo(path).Length == 0) return "文件是空的";

            using var z = ZipFile.OpenRead(path);
            var prop = z.Entries.FirstOrDefault(e =>
            {
                var n = e.FullName.Replace('\\', '/').TrimStart('.', '/');
                return n.Equals("module.prop", StringComparison.OrdinalIgnoreCase);
            });
            if (prop == null)
                return "压缩包根目录没有 module.prop（可能不是模块包，或者里面多套了一层文件夹）";

            using var sr = new StreamReader(prop.Open());
            var text = sr.ReadToEnd();
            if (text.IndexOf("id=", StringComparison.OrdinalIgnoreCase) < 0)
                return "module.prop 里缺少 id 字段";
            return null;
        }
        catch (InvalidDataException)
        {
            return "不是有效的 zip（可能是 tar.gz 之类改了后缀）";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task SendKeyAsync(int keyCode)
    {
        try { if (_modules != null) await _modules.SendKeyAsync(keyCode); }
        catch { }
    }


    /// <summary>在指定目录里新建文件夹（目录树右键用）</summary>
    public async Task NewFolderInAsync(string dir, string name)
    {
        if (_fs == null || string.IsNullOrWhiteSpace(name)) return;
        InvalidatePrefetch();
        try
        {
            await _fs.MkdirAsync(Sh.Join(dir, name));
            StatusText = $"已新建文件夹：{Sh.Join(dir, name)}";
            await RefreshTreeAsync();
            if (string.Equals(dir.TrimEnd('/'), CurrentPath.TrimEnd('/'), StringComparison.Ordinal))
                await NavigateAsync(CurrentPath);
        }
        catch (Exception ex) { StatusText = "新建失败：" + ex.Message; }
    }

    /// <summary>在指定目录里新建空文件（目录树右键用）</summary>
    public async Task NewFileInAsync(string dir, string name)
    {
        if (_fs == null || string.IsNullOrWhiteSpace(name)) return;
        InvalidatePrefetch();
        try
        {
            await _fs.TouchAsync(Sh.Join(dir, name));
            StatusText = $"已新建文件：{Sh.Join(dir, name)}";
            if (string.Equals(dir.TrimEnd('/'), CurrentPath.TrimEnd('/'), StringComparison.Ordinal))
                await NavigateAsync(CurrentPath);
        }
        catch (Exception ex) { StatusText = "新建失败：" + ex.Message; }
    }

    /// <summary>删除目录树里的一个目录</summary>
    public async Task DeleteNodeAsync(string dir)
    {
        if (_fs == null) return;
        InvalidatePrefetch();
        try
        {
            await _fs.DeleteAsync(new[] { dir });
            StatusText = "已删除：" + dir;
            await RefreshTreeAsync();
        }
        catch (Exception ex) { StatusText = "删除失败：" + ex.Message; }
    }

    /// <summary>重命名目录树里的一个目录</summary>
    public async Task RenameNodeAsync(string dir, string newName)
    {
        if (_fs == null) return;
        if (!IsValidName(newName)) { StatusText = "名称不能包含 / ，也不能是 . 或 .."; return; }
        if (string.Equals(Sh.BaseName(dir), newName, StringComparison.Ordinal)) return;
        InvalidatePrefetch();
        try
        {
            await _fs.RenameAsync(dir, newName);
            StatusText = "已重命名：" + newName;
            await RefreshTreeAsync();
        }
        catch (Exception ex) { StatusText = "重命名失败：" + ex.Message; }
    }

    /// <summary>退出前清理：设备上的临时解包目录 + 本机临时文件（不留痕迹、不越用越大）</summary>
    public async Task CleanupAsync()
    {
        // 1) 设备端：只删我们自己创建过的临时目录/文件
        try
        {
            if (IsArchiveMode) await CloseArchiveAsync();
        }
        catch { }

        try
        {
            if (_fs != null)
                await _fs.RunCommandAsync(
                    $"rm -rf {DevicePaths.LocalTmp}/.dashfile* {DevicePaths.LocalTmp}/dashfile_* " +
                    $"{DevicePaths.LocalTmp}/.df_* 2>/dev/null",
                    true);
        }
        catch { }

        // 2) 本机临时文件
        try
        {
            var tmp = Path.GetTempPath();
            foreach (var f in Directory.GetFiles(tmp, "dashfile*")) { try { File.Delete(f); } catch { } }
            foreach (var d in Directory.GetDirectories(tmp, "dashfile*")) { try { Directory.Delete(d, true); } catch { } }
            // 拖出用的临时目录（文件夹会先打成 tar.gz 放这里）
            foreach (var d in Directory.GetDirectories(tmp, "DashFileDrag")) { try { Directory.Delete(d, true); } catch { } }
            // 写入授权表时用的中转文件
            foreach (var f in Directory.GetFiles(tmp, "dashfile_w_*")) { try { File.Delete(f); } catch { } }
        }
        catch { }

        // 3) 图标缓存不再按体积清空：读过一次就一直留着，
        //    刷新时同包名直接命中缓存，不再重新读 APK（重新读要 unzip + 解 arsc，很贵）。
    }

    // ---------------- 压缩包（浏览 / 解压 / 回写） ----------------

    private const string KsuBusyboxPath = DevicePaths.KsuBusybox;
    private const string MagiskBusyboxPath = DevicePaths.MagiskBusybox;

    [ObservableProperty] private bool _isArchiveMode;
    [ObservableProperty] private string _archiveName = "";
    private string _archivePath = "";
    private string _archiveTempDir = "";

    public string ArchiveBanner => IsArchiveMode ? $"压缩包编辑中：{ArchiveName}（改动保存在临时目录，点「保存到压缩包」才会写回）" : "";

    private static string UnzipCmd(string zip, string dest) =>
        $"mkdir -p {Sh.Q(dest)}; " +
        $"if [ -x {KsuBusyboxPath} ]; then {KsuBusyboxPath} unzip -o -q {Sh.Q(zip)} -d {Sh.Q(dest)}; " +
        $"elif [ -x {MagiskBusyboxPath} ]; then {MagiskBusyboxPath} unzip -o -q {Sh.Q(zip)} -d {Sh.Q(dest)}; " +
        $"else unzip -o -q {Sh.Q(zip)} -d {Sh.Q(dest)}; fi; " +
        "echo RC=$?";

    /// <summary>右键解压：解到同目录下的同名文件夹</summary>
    public async Task ExtractArchiveAsync(PosixEntry e, string folderName)
    {
        InvalidatePrefetch();
        if (_fs == null) return;
        var dest = Sh.Join(Sh.Parent(e.FullPath), folderName);
        StatusText = $"正在解压 {e.Name} …";
        var r = await _fs.RunCommandAsync(UnzipCmd(e.FullPath, dest), true);
        var m = System.Text.RegularExpressions.Regex.Match(r, @"RC=(\d+)");
        if (m.Success && m.Groups[1].Value == "0")
        {
            StatusText = $"已解压到 {dest}";
            await NavigateAsync(CurrentPath);
        }
        else
        {
            StatusText = "解压失败：" + FirstUsefulLine(r);
            LogLine("UNZIP => " + r);
        }
    }

    /// <summary>双击 zip：解到临时目录，进入“压缩包编辑”模式</summary>
    public async Task<bool> OpenArchiveAsync(PosixEntry e)
    {
        if (_fs == null) return false;

        var tmp = DevicePaths.TempDirectory("zip");
        StatusText = $"正在打开压缩包 {e.Name} …";

        var r = await _fs.RunCommandAsync(
            $"rm -rf {Sh.Q(tmp)}; " + UnzipCmd(e.FullPath, tmp), true);

        var m = System.Text.RegularExpressions.Regex.Match(r, @"RC=(\d+)");
        if (!m.Success || m.Groups[1].Value != "0")
        {
            StatusText = "打开失败：不是有效的 zip，或者压缩包已加密";
            LogLine("OPEN ZIP => " + r);
            return false;
        }

        _archivePath = e.FullPath;
        _archiveTempDir = tmp;
        ArchiveName = e.Name;
        IsArchiveMode = true;
        OnPropertyChanged(nameof(ArchiveBanner));

        await NavigateAsync(tmp);
        StatusText = $"已打开 {e.Name}（临时解包，修改后记得「保存到压缩包」）";
        return true;
    }

    /// <summary>把临时目录的改动重新打包，覆盖回原 zip</summary>
    public async Task<bool> SaveArchiveAsync()
    {
        InvalidatePrefetch();
        if (_fs == null || !IsArchiveMode) return false;

        var tgz = DevicePaths.TempFile("save", ".tgz");
        var localTgz = DevicePaths.LocalTempFile("save", ".tgz");
        var localZip = Path.Combine(Path.GetTempPath(), ArchiveName);

        try
        {
            StatusText = "正在重新打包…";
            await _fs.RunCommandAsync($"rm -f {Sh.Q(tgz)}", true);
            await _fs.RunCommandAsync(
                $"tar -czf {Sh.Q(tgz)} -C {Sh.Q(_archiveTempDir)} . 2>/dev/null", true);

            var sizeRaw = await _fs.RunCommandAsync(
                $"if [ -s {Sh.Q(tgz)} ]; then stat -c %s {Sh.Q(tgz)}; else echo 0; fi", true);
            var mSize = System.Text.RegularExpressions.Regex.Match(sizeRaw, @"(?m)^\s*(\d+)\s*$");
            if (!mSize.Success || long.Parse(mSize.Groups[1].Value, CultureInfo.InvariantCulture) <= 0)
            {
                StatusText = "保存失败：打包结果为空";
                return false;
            }

            await _fs.DownloadAsync(tgz, localTgz);
            ConvertTarGzToZip(localTgz, localZip);

            StatusText = "正在写回设备…";
            await _fs.UploadAsync(localZip, Sh.Parent(_archivePath));

            StatusText = $"已保存到 {_archivePath}";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "保存失败：" + ex.Message;
            LogLine("SAVE ZIP => " + ex.Message);
            return false;
        }
        finally
        {
            try { await _fs.RunCommandAsync($"rm -f {Sh.Q(tgz)}", true); } catch { }
            try { if (File.Exists(localTgz)) File.Delete(localTgz); } catch { }
            try { if (File.Exists(localZip)) File.Delete(localZip); } catch { }
        }
    }

    /// <summary>退出压缩包模式，清理临时目录</summary>
    public async Task CloseArchiveAsync()
    {
        if (!IsArchiveMode) return;
        var tmp = _archiveTempDir;
        var back = Sh.Parent(_archivePath);

        IsArchiveMode = false;
        ArchiveName = "";
        OnPropertyChanged(nameof(ArchiveBanner));

        try
        {
            if (_fs != null) await _fs.RunCommandAsync($"rm -rf {Sh.Q(tmp)}", true);
        }
        catch { }

        _archiveTempDir = "";
        _archivePath = "";
        if (back.Length > 0) await NavigateAsync(back);
    }

    /// <summary>把已安装模块重新打包成真正的 zip 并保存到本地</summary>
    public async Task<bool> ExportModuleAsync(ModuleInfo m, string localZipPath)
    {
        if (_fs == null) { StatusText = "未连接设备"; return false; }

        var remoteTgz = DevicePaths.TempFile("mod_" + m.Id, ".tgz");
        var localTgz = Path.Combine(Path.GetTempPath(), m.Id + ".export.tgz");

        try
        {
            ModulesStatus = $"正在打包「{m.Name}」…";
            await _fs.RunCommandAsync($"rm -f {Sh.Q(remoteTgz)}", true);

            // 注意：-C 到模块目录里再打 "."，这样 module.prop 才会在压缩包第一层
            await _fs.RunCommandAsync(
                $"tar -czf {Sh.Q(remoteTgz)} -C {Sh.Q(DevicePaths.ModuleDir(m.Id))} . 2>/dev/null", true);

            var sizeRaw = await _fs.RunCommandAsync(
                $"if [ -s {Sh.Q(remoteTgz)} ]; then stat -c %s {Sh.Q(remoteTgz)}; else echo 0; fi", true);
            var mSize = System.Text.RegularExpressions.Regex.Match(sizeRaw, @"(?m)^\s*(\d+)\s*$");
            long size = mSize.Success ? long.Parse(mSize.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            if (size <= 0)
            {
                ModulesStatus = $"打包失败：「{m.Name}」";
                StatusText = "提取失败：远端未生成压缩包";
                return false;
            }

            await _fs.DownloadAsync(remoteTgz, localTgz);

            // 设备上没有 zip，本地转成标准 zip（保留可执行权限，KernelSU 才认）
            ModulesStatus = $"正在生成 zip：「{m.Name}」…";
            var files = ConvertTarGzToZip(localTgz, localZipPath);

            long len = 0;
            try { len = new FileInfo(localZipPath).Length; } catch { }
            if (len <= 0 || files <= 0)
            {
                StatusText = "提取失败：生成的 zip 无效";
                ModulesStatus = "提取失败";
                return false;
            }

            StatusText = $"已提取「{m.Name}」→ {localZipPath}（{len / 1024.0:0.#} KB，{files} 个文件）";
            ModulesStatus = $"已提取「{m.Name}」";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "提取失败：" + ex.Message;
            ModulesStatus = "提取失败";
            return false;
        }
        finally
        {
            try { await _fs.RunCommandAsync($"rm -f {Sh.Q(remoteTgz)}", true); } catch { }
            try { if (File.Exists(localTgz)) File.Delete(localTgz); } catch { }
        }
    }

    /// <summary>把 tar.gz 转成 zip：内容放在根目录，并写回 Unix 权限位</summary>
    private static int ConvertTarGzToZip(string tarGzPath, string zipPath)
    {
        var count = 0;
        using var input = File.OpenRead(tarGzPath);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gz);

        var dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var outFs = File.Create(zipPath);
        using var zip = new ZipArchive(outFs, ZipArchiveMode.Create);

        while (reader.GetNextEntry() is { } entry)
        {
            var name = (entry.Name ?? "").Replace('\\', '/');
            name = name.TrimStart('.', '/');
            if (name.Length == 0) continue;

            var mode = (int)entry.Mode;

            if (entry.EntryType == TarEntryType.Directory)
            {
                if (!name.EndsWith('/')) name += "/";
                zip.CreateEntry(name, CompressionLevel.NoCompression);
                continue;
            }

            if (entry.EntryType == TarEntryType.SymbolicLink)
            {
                var se = zip.CreateEntry(name, CompressionLevel.NoCompression);
                se.ExternalAttributes = (int)((uint)(0xA000 | (mode & 0xFFF)) << 16);
                using var sw = new StreamWriter(se.Open());
                sw.Write(entry.LinkName ?? "");
                continue;
            }

            var ze = zip.CreateEntry(name, CompressionLevel.Optimal);
            ze.ExternalAttributes = (int)((uint)(mode & 0xFFF) << 16);
            if (entry.DataStream != null)
            {
                using var es = ze.Open();
                entry.DataStream.CopyTo(es);
            }
            count++;
        }
        return count;
    }

    public async Task ToggleModuleAsync(ModuleInfo m)
        => await SetModuleEnabledAsync(m, !m.Enabled);

    /// <summary>明确设置模块开关（插件 API 用；避免“取反”在旧对象上算错）。</summary>
    public async Task<bool> SetModuleEnabledAsync(ModuleInfo m, bool enabled)
    {
        if (_modules == null) return false;
        try
        {
            await _modules.SetEnabledAsync(m, enabled);
            await LoadModulesAsync();
            StatusText = enabled ? $"已启用模块 {m.Name}（重启生效）" : $"已停用模块 {m.Name}（重启生效）";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "操作失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>标记卸载模块；返回是否真的成功（插件 API 要用真实结果，不能一律 true）</summary>
    public async Task<bool> UninstallModuleAsync(ModuleInfo m)
    {
        if (_modules == null) return false;
        try
        {
            await _modules.UninstallAsync(m);
            await LoadModulesAsync();
            StatusText = $"已标记卸载 {m.Name}（重启后移除）";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "卸载失败：" + ex.Message;
            return false;
        }
    }

    public async Task<string> RunModuleActionAsync(ModuleInfo m)
    {
        if (_modules == null) return "未就绪";
        try { return await _modules.RunActionAsync(m); }
        catch (Exception ex) { return "执行失败：" + ex.Message; }
    }

    public async Task<string?> PrepareModuleWebUiAsync(ModuleInfo m)
    {
        if (_modules == null || _fs == null) return null;
        try
        {
            StatusText = "正在导出 WebUI…";
            var remote = await _modules.ExportWebRootAsync(m);
            var local = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DashFileWebUI", m.Id);
            System.IO.Directory.CreateDirectory(local);
            var pull = await _runner.RunAsync(new[] { "-s", _fs.Serial, "pull", remote, local }, _runner.TransferTimeout);
            if (!pull.Ok) return null;
            StatusText = "WebUI 已导出：" + local;
            return local;
        }
        catch (Exception ex) { StatusText = "导出失败：" + ex.Message; return null; }
    }
    [RelayCommand]
    private async Task RefreshDeviceInfoAsync()
    {
        await LoadDeviceInfoAsync(_lastSerial, "", "");
        // 以前这里只刷设备参数，root 授权数/模块数会一直停在连接时读到的旧值
        await LoadRootInfoAsync();
    }

    private string _lastSerial = "";

    /// <summary>最后一次见过的序列号（含 fastboot/Recovery 阶段），重启按钮用它兜底</summary>
    private string _lastKnownSerial = "";
    private DateTime _lastFbProbe = DateTime.MinValue;
    private DeviceInfo? _lastFbDevice;

    // ---- 设备信息 ----
    [ObservableProperty] private string _devStatus = "—";
    [ObservableProperty] private string _devLink = "—";
    [ObservableProperty] private string _devSerial = "—";
    [ObservableProperty] private string _devModel = "—";
    [ObservableProperty] private string _devCodename = "—";
    [ObservableProperty] private string _devAndroid = "—";
    [ObservableProperty] private string _devUnlock = "—";
    [ObservableProperty] private string _devVersion = "—";
    [ObservableProperty] private string _devKernel = "—";
    [ObservableProperty] private string _devBuildDate = "—";
    [ObservableProperty] private string _devSlot = "—";
    [ObservableProperty] private string _devCpuVendor = "—";
    [ObservableProperty] private string _devCpuModel = "—";
    [ObservableProperty] private string _devCpuName = "—";
    [ObservableProperty] private string _devOs = "—";
    [ObservableProperty] private string _devSelinux = "—";
    [ObservableProperty] private string _devRam = "—";
    [ObservableProperty] private string _devStorage = "—";
    [ObservableProperty] private string _devRing = "—";
    [ObservableProperty] private string _batteryShort = "—";
    [ObservableProperty] private string _devScreen = "—";
    [ObservableProperty] private string _devCores = "—";

    // ---- 右侧看板 ----
    [ObservableProperty] private string _rootImpl = "—";
    [ObservableProperty] private string _managerVersion = "—";
    [ObservableProperty] private string _managerMode = "—";
    [ObservableProperty] private string _rootHint = "";
    [ObservableProperty] private string _superuserCount = "—";
    [ObservableProperty] private string _moduleCount = "—";
    [ObservableProperty] private IReadOnlyList<double> _cpuPoints = System.Array.Empty<double>();
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private string _ramText = "—";

    private readonly List<double> _cpuSeries = new();
    private Avalonia.Threading.DispatcherTimer? _monitor;

    partial void OnBatteryShortChanged(string value) => OnPropertyChanged(nameof(RightStatusText));
    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(RightStatusText));

    [ObservableProperty] private bool _showToolbar = false;
    [ObservableProperty] private bool _showLog = false;

    partial void OnSelectedDeviceChanged(DeviceInfo? value)
    {
        if (_suppressConnect) return;
        ConnectAsync(value).Safe("连接设备", m => StatusText = m);
    }

    // 刷新设备列表时 ComboBox 会先把选中项置空再恢复；同一台设备不需要重连
    private bool _suppressConnect;

    // ---------------- 连接 ----------------

    public async Task InitializeAsync()
    {
        // 窗口已经显示出来后再做重活（adb 启动、设备扫描），避免开屏卡顿
        await Task.Yield();
        StartWatchdog();
        await RefreshDevicesAsync();
        InitializePluginsAsync().Safe("加载插件", m => StatusText = m);
        PerfProbeAsync().Safe("性能探针");
    }

    // ---------------- 设备连接看门狗 ----------------

    private readonly Avalonia.Threading.DispatcherTimer _watch =
        new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private bool _watchBusy;
    private string _lastSignature = "";
    private readonly Avalonia.Threading.DispatcherTimer _wake =
        new() { Interval = TimeSpan.FromMilliseconds(60) };

    [ObservableProperty] private bool _isEdlMode;
    [ObservableProperty] private string _edlText = "";

    private void StartWatchdog()
    {
        if (_watch.IsEnabled) return;
        _watch.Tick += async (_, _) => await WatchTickAsync();
        _watch.Start();

        // 系统一有 USB 设备变化就立刻来一次（USB 插拔 <100ms 内响应），60ms 防抖
        _wake.Tick += async (_, _) =>
        {
            _wake.Stop();
            await WatchTickAsync();
        };
    }

    /// <summary>由窗口的 WM_DEVICECHANGE 调用</summary>
    public void NotifyDeviceTreeChanged()
    {
        _wake.Stop();
        _wake.Start();
    }

    private async Task WatchTickAsync()
    {
        if (_watchBusy) return;
        _watchBusy = true;
        try
        {
            var list = await _client.ListDevicesAsync().ConfigureAwait(true);

            // ADB 里没有在线设备时，很可能是刚重启到 fastboot / fastbootd：
            //   这时必须去问 fastboot，否则会误判成"设备已断开"，序列号一丢就再也重启不了。
            // 判据必须是"adb 一条记录都没有"，而不是"没有 online 设备"：
            //   recovery / sideload / unauthorized 都是"有记录但非 online"，
            //   按后者判断会把过期的 fastboot 缓存贴回去，导致设备明明已经进了 recovery，
            //   程序还以为在 fastboot —— 之后所有按钮都在给不存在的设备发命令。
            if (list.Count == 0)
            {
                // 探测有 2s 节流、看门狗 1.2s 一轮：缓存结果避免"Fastboot ↔ 未连接"来回跳
                var fb = await ProbeFastbootAsync();
                if (fb != null) _lastFbDevice = fb;
                fb ??= _lastFbDevice;
                if (fb != null && list.All(d => d.Serial != fb.Serial)) list.Add(fb);
            }
            else
            {
                _lastFbDevice = null;      // adb 里有任何记录，就不再保留 fastboot 记忆
            }

            // adb、fastboot 都看不见时，最后再问一句 Windows：设备是不是还插着
            _usbState = list.Count == 0 ? await ProbeUsbAsync() : "";

            var sig = string.Join(",", list.OrderBy(d => d.Serial)
                                           .Select(d => d.Serial + ":" + d.State));
            if (string.Equals(sig, _lastSignature, StringComparison.Ordinal)) return;
            _lastSignature = sig;

            LogLine("watchdog → " + (sig.Length == 0 ? "无设备" : sig));
            ApplyDeviceList(list);
        }
        catch { }
        finally { _watchBusy = false; }
    }

    private async Task<DeviceInfo?> ProbeFastbootAsync()
    {
        if ((DateTime.Now - _lastFbProbe).TotalMilliseconds < 2000) return null;
        _lastFbProbe = DateTime.Now;

        var serials = await _power.ListFastbootSerialsAsync().ConfigureAwait(true);
        var serial = serials.FirstOrDefault();
        Perf($"fastboot probe -> {serials.Count} 个设备{(serial == null ? "" : "：" + serial)}");
        return serial == null ? null : new DeviceInfo { Serial = serial, State = "fastboot" };
    }

    private string _usbState = "";      // ""=无 / "edl"=9008 / "android"=有安卓设备但调试口不可用

    /// <summary>
    /// 用 pnputil 看 Windows 这一层有没有这台安卓设备。
    /// adb / fastboot 都不可用时，这是唯一能判断"设备其实还插着"的办法：
    ///   Recovery 没开 ADB、或者系统正在启动时，两个端口都不响应，但 USB 设备还在。
    /// </summary>
    private async Task<string> ProbeUsbAsync()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("pnputil", "/enum-devices /connected")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return "";
            var text = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();

            if (text.Contains("9008", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("QDLoader", StringComparison.OrdinalIgnoreCase)) return "edl";

            if (text.Contains("Android", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("ADB Interface", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Bootloader", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Fastboot", StringComparison.OrdinalIgnoreCase)) return "android";

            // 加固：设备名不一定带 "Android"（各家 OEM 五花八门），再按厂商 VID 认一次。
            //   这样能区分"真没插线"和"插着但调试口不可用（Recovery / 正在启动）"。
            string[] vids =
            {
                "VID_18D1", // Google / 通用 Android
                "VID_22D9", // OPPO / 一加老机型
                "VID_2717", // 小米
                "VID_2A70", // 一加
                "VID_2D95", // vivo
                "VID_05C6", // 高通（含 9008）
                "VID_12D1", // 华为
                "VID_04E8", // 三星
                "VID_0FCE", // 索尼
                "VID_2916", // 中兴
                "VID_2E04", // 荣耀
            };
            foreach (var vid in vids)
                if (text.Contains(vid, StringComparison.OrdinalIgnoreCase)) return "android";
        }
        catch { }
        return "";
    }

    private async Task DetectEdlAsync()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("pnputil", "/enum-devices /connected")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return;
            var text = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();

            var edl = text.Contains("9008", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("QDLoader", StringComparison.OrdinalIgnoreCase);
            if (edl != IsEdlMode)
            {
                IsEdlMode = edl;
                EdlText = edl ? "9008 (EDL) 紧急下载模式" : "";
            }
        }
        catch { }
    }

    /// <summary>设备断开时立刻把设备信息清空，避免显示上一次的旧数据</summary>
    /// <summary>
    /// 设备处于 fastboot / fastbootd / sideload / recovery：ADB 用不了，但设备还在。
    /// 这里把界面填成"有设备但不可浏览"，并保留序列号，让「快捷重启」仍然可用。
    /// </summary>
    private void ApplyFastbootOnly(DeviceInfo dev)
    {
        var st = (dev.State ?? "").ToLowerInvariant();
        var name = st.Contains("fastboot") ? "Fastboot" : "Recovery";

        _lastKnownSerial = dev.Serial;

        Devices.Clear();
        Devices.Add(dev);

        RootBadge = name;
        DeviceModeText = name;
        DeviceSub = $"设备已进入 {name} 模式（ADB 暂不可用）—— 用「快捷重启」可以切回系统或去别的模式";
        StatusText = DeviceSub;

        _fs = null;
        IsRoot = false;
        ClearDeviceInfo();

        DevStatus = name;
        DevSerial = dev.Serial;
        DevLink = st.Contains("fastboot") ? "Fastboot（USB）" : "Recovery（USB）";
        DevModel = dev.Serial;

        // ConnectAsync 里有守卫，选中不会去硬连 ADB，只是让下拉框和重启按钮拿到序列号
        if (SelectedDevice?.Serial != dev.Serial) SelectedDevice = dev;
    }

    private void ClearDeviceInfo()
    {
        DevStatus = "未连接";
        DevLink = "—"; DevSerial = "—"; DevModel = "—"; DevCodename = "—"; DevAndroid = "—";
        DevUnlock = "—"; DevVersion = "—"; DevKernel = "—"; DevBuildDate = "—"; DevSlot = "—";
        _blFlashLocked = ""; _blVerifiedBoot = ""; _blVbmetaState = ""; _blSecureLock = ""; _blCmdline = "";
        DevCpuVendor = "—"; DevCpuModel = "—"; DevCpuName = "—"; DevOs = "—"; DevSelinux = "—";
        DevRam = "—"; DevStorage = "—"; DevScreen = "—"; DevCores = "—"; DevRing = "—";
        DevBatteryText = "—"; BatteryShort = "—";
        RootImpl = "—"; ManagerVersion = "—"; ManagerMode = "—"; RootHint = "";
        SuperuserCount = "—"; ModuleCount = "—";
        CpuText = "—"; RamText = "—"; RamPercent = 0;
        CpuPoints = Array.Empty<double>();
        _lastIdle = 0; _lastTotal = 0;
    }

    /// <summary>把最新设备列表落到 UI（插拔、授权、掉线都走这里）</summary>
    private void ApplyDeviceList(List<DeviceInfo> list)
    {
        Perf($"apply list: {list.Count} 项 {(list.Count > 0 ? list[0].Serial + "/" + list[0].State : "(空)")}");
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var keep = SelectedDevice?.Serial;
            Devices.Clear();
            foreach (var d in list) Devices.Add(d);

            var online = list.FirstOrDefault(d => d.IsOnline);

            if (list.Count == 0)
            {
                IsEdlMode = _usbState == "edl";
                EdlText = IsEdlMode ? "9008 (EDL) 紧急下载模式" : "";

                if (IsEdlMode)
                {
                    RootBadge = "9008 (EDL)";
                    DeviceSub = "设备处于 9008 (EDL) 紧急下载模式，ADB 不可用；长按电源键 10~15 秒可强制重启回系统";
                }
                else if (_usbState == "android")
                {
                    RootBadge = "等待中";
                    DeviceSub = "设备已通过 USB 连接，但 ADB / Fastboot 都不可用（可能在 Recovery，或系统正在启动）";
                }
                else
                {
                    RootBadge = "未连接";
                    DeviceSub = "没有检测到设备 —— 插上数据线后会自动识别";
                }
                DeviceModeText = "未连接";
                StatusText = "设备已断开";
                _monitor?.Stop();
                _sampling = false;
                _fs = null; IsRoot = false;
                Entries.Clear(); IsEmpty = true;
                Modules.Clear(); ModulesEmpty = true;
                Apps.Clear(); AppsStatus = "未连接设备"; _apps = null; _modules = null;
                ClearDeviceInfo();
                _lastSerial = "";
                _plugins?.RaiseDeviceChanged(new DeviceSnapshot(false, false, "", "", "", ""));
                return;
            }

            if (online == null)
            {
                var bad = list[0];
                var st = (bad.State ?? "").ToLowerInvariant();

                // fastboot / fastbootd / sideload / recovery：设备还在，只是 ADB 用不了。
                // 这时不要清序列号，也不要去 ConnectAsync（必然失败），只要让重启按钮可用。
                if (st.Contains("fastboot") || st.Contains("sideload") || st.Contains("recovery"))
                {
                    ApplyFastbootOnly(bad);
                    return;
                }

                RootBadge = st.Contains("unauthorized") ? "待授权" : "离线";
                DeviceSub = st.Contains("unauthorized")
                    ? "手机弹出了「允许 USB 调试」？点允许后会自动连上"
                    : $"设备状态：{bad.State}";
                StatusText = DeviceSub;
                _monitor?.Stop();
                _sampling = false;
                _fs = null; IsRoot = false;
                ClearDeviceInfo();
                return;
            }

            _lastKnownSerial = online.Serial;
            var pick = list.FirstOrDefault(d => d.Serial == keep) ?? online;
            if (SelectedDevice?.Serial != pick.Serial)
            {
                // 只是 Devices.Clear() 把 ComboBox 的选中项清掉了：已经连着同一台设备时
                //   不要重新走 ConnectAsync（重连会重建 fs、重新扫模块，启动时白卡 500ms）。
                var same = _fs != null && string.Equals(_fs.Serial, pick.Serial, StringComparison.Ordinal);
                if (same)
                {
                    _suppressConnect = true;
                    try { SelectedDevice = pick; } finally { _suppressConnect = false; }
                }
                else SelectedDevice = pick;      // 触发 ConnectAsync
            }
            else if (!_connecting && _fs == null)
            {
                ConnectAsync(pick).Safe("连接设备", m => StatusText = m);
            }
        });
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        try
        {
            IsBusy = true;
            StatusText = "正在查找已连接的设备…";
            var swFind = System.Diagnostics.Stopwatch.StartNew();
            await _client.EnsureServerAsync();
            Perf($"startup ensure-adb-server {swFind.ElapsedMilliseconds}ms");

            List<DeviceInfo> list = new();
            bool anyOnline = false;

            for (int round = 0; round < 2 && !anyOnline; round++)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    list = await _client.ListDevicesAsync();
                    anyOnline = list.Any(d => d.IsOnline);
                    if (anyOnline) break;
                    try { await Task.Delay(350); } catch { break; }
                }

                if (!anyOnline && round == 0)
                {
                    // 注意：不要自动 kill-server！部分设备重启服务后必须重新插拔才能识别
                    LogLine($"暂未检测到在线设备（当前 {list.Count} 条），稍后重试…");
                    try { await Task.Delay(600); } catch { }
                }
            }

            Perf($"startup adb devices {swFind.ElapsedMilliseconds}ms list={list.Count}");
            LogLine($"adb devices → {list.Count} 条，在线 {list.Count(d => d.IsOnline)} 条");

            var keep = SelectedDevice?.Serial;
            Devices.Clear();
            foreach (var d in list) Devices.Add(d);

            if (Devices.Count == 0 || !anyOnline)
            {
                // 常见场景：手机刚重启到 fastboot，ADB 列表是空的
                var fb = await ProbeFastbootAsync() ?? _lastFbDevice;
                if (fb != null) { ApplyFastbootOnly(fb); return; }

                RootBadge = "未连接";
                DeviceSub = Devices.Count == 0
                    ? "没有检测到设备 —— 请检查数据线、USB 调试是否已打开（可点「重新查找设备」）"
                    : $"检测到 {Devices.Count} 台设备但未授权/离线，请在手机上点「允许 USB 调试」";
                StatusText = "未检测到设备";
                _monitor?.Stop();
                _sampling = false;
                IsRoot = false;
                _fs = null;
                Entries.Clear();
                IsEmpty = true;
                return;
            }

            var pick = Devices.FirstOrDefault(d => d.Serial == keep)
                       ?? Devices.FirstOrDefault(d => d.IsOnline) ?? Devices[0];
            if (ReferenceEquals(pick, SelectedDevice)) await ConnectAsync(pick);
            else SelectedDevice = pick;
        }
        catch (Exception ex)
        {
            StatusText = "查找设备失败：" + ex.Message;
            LogLine(ex.Message);
        }
        finally { IsBusy = false; }
    }

    private async Task ConnectAsync(DeviceInfo? dev)
    {
        if (dev == null) return;

        // fastboot / fastbootd / sideload：ADB 根本连不上，别去试（会白等超时并把状态改成"连接失败"）
        var state = (dev.State ?? "").ToLowerInvariant();
        if (state.Length > 0 && state != "device" && !state.Contains("recovery"))
        {
            StatusText = $"设备处于 {dev.State} 模式，ADB 暂不可用";
            return;
        }

        if (_connecting) return;
        _connecting = true;
        try
        {
            IsBusy = true;
            StatusText = $"正在连接 {dev.Serial} …";
            var swConn = System.Diagnostics.Stopwatch.StartNew();

            DevStatus = "正在连接…";
            var probe = new RemoteFileSystem(_runner, dev.Serial, false);
            IsRoot = await probe.DetectRootAsync();
            Perf($"startup detect-root {swConn.ElapsedMilliseconds}ms root={IsRoot}");

            _fs = new RemoteFileSystem(_runner, dev.Serial, IsRoot, FileTransfers) { };
            _fs.Log += LogLine;

            // 换设备/重连时必须重建：这些服务内部抓着旧的 _fs（旧序列号）
            _apps = null;
            _modules = null;
            _root = null;
            _dirCache.Clear();

            // 换设备/重连必须把"按旧设备内容缓存的一切"一起作废，并取消在途任务：
            //   以前只清 _dirCache，_apps 置空却不 cancel _appCts —— 在途 LoadAppsAsync 里
            //   _apps.ListAsync 直接 NRE，旧设备的条目还会 Add 进新设备的列表。
            try { _appCts?.Cancel(); } catch { }
            _appCts = null;
            try { _prefetchCts?.Cancel(); } catch { }
            _prefetchCts = null;
            try { _enrichCts?.Cancel(); } catch { }
            _enrichCts = null;

            _listCache.Clear();
            _prefetch.Clear();
            _treeCache.Clear();
            _hasKids.Clear();
            _enriched.Clear();
            _treeLoads.Clear();
            _nodes.Clear();
            _rootNode = null;
            TreeRows.Clear();

            Apps.Clear();
            AppsStatus = "";

            RootBadge = IsRoot ? "已获取 ROOT" : "普通权限";

            Perf($"startup fs-ready {swConn.ElapsedMilliseconds}ms");

            // 三条 getprop/dumpsys 并发跑（每条都是一次 adb 往返，串行白等）
            var modeTask = _power.DetectModeAsync(dev.Serial);
            string model = "", rel = "";
            try
            {
                var props = await Task.WhenAll(
                    _client.GetPropAsync(dev.Serial, "ro.product.model"),
                    _client.GetPropAsync(dev.Serial, "ro.build.version.release"));
                model = props[0];
                rel = props[1];
            }
            catch { }
            Perf($"startup props {swConn.ElapsedMilliseconds}ms");
            DeviceSub = $"设备：{model}    Android {rel}    " +
                        (IsRoot ? "可用 root 访问系统目录" : "无 root，仅能访问内部存储");

            HintText = IsRoot
                ? "提示：双击文件夹进入 · 选中后可用「权限」改 777 · 「执行」跑 shell 脚本"
                : "提示：当前没有 root，只能访问内部存储；如需进入 /data，请在手机上授权 root";

            try
            {
                var mode = await modeTask;
                DeviceModeText = PowerService.ModeText(mode);
            }
            catch { DeviceModeText = "未知"; }

            LogLine($"已连接 {dev.Serial}（服务已重建）");

            // 先出文件列表，再补设备详情 / root 授权 / 模块：
            //   以前是"详情 → root → 模块 → 目录"四级串行，第一次看见文件要等 1.2s。
            var startPath = AppEnvironment.StartPath;
            // 有 root 才从 "/" 起步（否则根目录基本全是 Permission denied）；只有 adb 时直接进内部存储
            var navTask = NavigateAsync(string.IsNullOrWhiteSpace(startPath)
                ? (IsRoot ? "/" : DevicePaths.Sdcard)
                : startPath);
            var infoTask = LoadDeviceInfoAsync(dev.Serial, model, rel);
            var rootTask = LoadRootInfoAsync();

            await navTask;
            Perf($"startup first-nav {swConn.ElapsedMilliseconds}ms");
            if (IsAppsTab) LoadAppsAsync().Safe("读取应用", m => StatusText = m);
            StartMonitor();

            await infoTask;
            Perf($"startup device-info {swConn.ElapsedMilliseconds}ms");
            await rootTask;
            Perf($"startup root-info {swConn.ElapsedMilliseconds}ms ready");
            _plugins?.RaiseDeviceChanged(CurrentDeviceSnapshot());
        }
        catch (Exception ex)
        {
            StatusText = "连接失败：" + ex.Message;
            LogLine(ex.Message);
        }
        finally
        {
            IsBusy = false;
            _connecting = false;

            // 连接过程中用户可能又换了设备：连完回头再看一眼
            var want = SelectedDevice;
            if (want != null && !string.Equals(want.Serial, dev.Serial, StringComparison.Ordinal))
                ConnectAsync(want).Safe("连接设备", m => StatusText = m);
        }
    }

    private async Task LoadRootInfoAsync()
    {
        if (_fs == null) return;
        try
        {
            // 统一走 provider：KernelSU 系 / Magisk 系 / APatch 各自的差异都收在 Services/Root 里，
            //   这里只负责把结果贴到界面上。
            _root = IsRoot ? await RootProviders.DetectAsync(_fs) : null;
            // provider 变了，模块安装/卸载命令也要跟着换
            _modules = null;

            RootImpl = _root?.DisplayName ?? "未检测到";
            UpdateBootloaderVerdict();   // 拿到 root 实现后重判：属性被伪装时这里才纠得回来
            ManagerVersion = _root?.Version ?? "—";
            ManagerMode = _root?.WorkMode ?? "—";
            RootHint = _root?.GrantHint ?? "";

            if (_root != null)
            {
                var grants = _root.CanReadGrants
                    ? await _root.ReadGrantsAsync()
                    : new List<RootGrant>();
                var allowedCount = grants.Count(g => g.Allowed);

                // GrantHint 依赖"读到表之后才知道的布局歧义"，读完要再取一次，否则提示永远是旧的
                RootHint = _root.GrantHint;

                // 读不了或写不了授权表的实现显示"—"而不是 0，避免误导
                SuperuserCount = _root.CanReadGrants
                    ? (grants.Count == 0 && !_root.CanManageGrants ? "—" : allowedCount.ToString())
                    : "—";
            }
            else
            {
                SuperuserCount = "—";
            }

            await LoadModulesAsync();
            ModuleCount = Modules.Count.ToString();

            Perf($"root provider -> id={_root?.Id} family={_root?.Family} name={_root?.DisplayName} " +
                 $"ver={_root?.Version} mode={_root?.WorkMode} canRead={_root?.CanReadGrants} " +
                 $"canGrant={_root?.CanManageGrants} count={SuperuserCount} modules={ModuleCount}");
        }
        catch { }
    }

    private DeviceSnapshot CurrentDeviceSnapshot()
        => new(_fs != null, IsRoot, _fs?.Serial ?? "", DevModel, DevAndroid, RootImpl);

    private void StartMonitor()
    {
        // 可重启：以前 Stop 过以后 if (_monitor != null) return 会让曲线永久不动
        if (_monitor != null)
        {
            _lastTotal = 0;
            _lastIdle = 0;
            if (!_monitor.IsEnabled) _monitor.Start();
            return;
        }
        _lastTotal = 0;
        _lastIdle = 0;
        _monitor = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _monitor.Tick += (_, _) => SampleAsync().Safe("刷新设备监控");
        _monitor.Start();
    }

    private long _lastIdle, _lastTotal;

    private bool _sampling;

    private async Task SampleAsync()
    {
        // 设备卡顿时 1s 一次 tick 会叠加多个 20s 超时的 adb 命令，越卡越多。
        if (_fs == null || _sampling) return;
        _sampling = true;
        try
        {
            // quiet：1 秒一次的就绪轮询不能写进运行日志（否则日志和日志页会被刷满）
            var raw = await _fs.RunCommandAsync(
                "head -1 /proc/stat; grep -E 'MemTotal|MemAvailable' /proc/meminfo", false, quiet: true);

            foreach (var line in raw.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("cpu "))
                {
                    var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5)
                    {
                        long total = 0;
                        for (int i = 1; i < parts.Length && i <= 8; i++)
                        {
                            // /proc/stat 的字段一定是数字；解析不出来就当 0，别把上一轮的抖动算进增量
                            if (long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) total += v;
                        }
                        long idle = 0;
                        if (long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idleV)) idle = idleV;
                        if (parts.Length > 5 &&
                            long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iow)) idle += iow;

                        if (_lastTotal > 0 && total > _lastTotal)
                        {
                            var dTotal = total - _lastTotal;
                            var dIdle = idle - _lastIdle;
                            var usage = dTotal > 0 ? (1.0 - (double)dIdle / dTotal) * 100.0 : 0;
                            usage = Math.Clamp(usage, 0, 100);
                            CpuText = usage.ToString("0.0") + " %";
                            _cpuSeries.Add(usage);
                            while (_cpuSeries.Count > 60) _cpuSeries.RemoveAt(0);
                            CpuPoints = _cpuSeries.ToArray();
                        }
                        _lastTotal = total; _lastIdle = idle;
                    }
                }
                else if (t.StartsWith("MemTotal:")) _memTotalKb = ParseKb(t);
                else if (t.StartsWith("MemAvailable:")) _memAvailKb = ParseKb(t);
            }

            if (_memTotalKb > 0)
            {
                var usedKb = _memTotalKb - _memAvailKb;
                RamPercent = Math.Clamp(usedKb * 100.0 / _memTotalKb, 0, 100);
                RamText = $"{usedKb / 1048576.0:0.0} / {_memTotalKb / 1048576.0:0.0} GB";
            }
        }
        catch (Exception ex) { AppLog.Add("采样失败：" + ex.Message); }
        finally { _sampling = false; }
    }

    private long _memTotalKb, _memAvailKb;

    private static long ParseKb(string line)
    {
        var digits = new string(line.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var v) ? v : 0;
    }

    // BL 判定用到的原始信号：属性可能是伪装值，/proc/cmdline 一般是真值
    private string _blFlashLocked = "";
    private string _blVerifiedBoot = "";
    private string _blVbmetaState = "";
    private string _blSecureLock = "";
    private string _blCmdline = "";

    private static string FirstNonEmpty(string a, string b) => !string.IsNullOrWhiteSpace(a) && a != "—" ? a : b;

    /// <summary>
    /// 重算解锁状态。root 可用是强证据：锁着 BL 的原厂机拿不到 root，
    /// 因此"属性说锁定 + root 可用"只可能是隐藏模块把状态伪装了。
    /// root 检测比设备信息慢，LoadRootInfoAsync 结束时会再调一次修正界面。
    /// </summary>
    private void UpdateBootloaderVerdict()
    {
        var verdict = BootloaderState.Judge(new BootloaderState.Signals
        {
            FlashLocked = _blFlashLocked,
            VerifiedBootState = _blVerifiedBoot,
            VbmetaState = _blVbmetaState,
            SecureBootLock = _blSecureLock,
            Cmdline = _blCmdline,
            RootAvailable = IsRoot || _root != null,
        });
        DevUnlock = BootloaderState.Describe(verdict);
    }

    private async Task LoadDeviceInfoAsync(string serial, string model, string androidVer)
    {
        if (string.IsNullOrWhiteSpace(serial)) return;
        _lastSerial = serial;

        try
        {
            DevStatus = "已连接";
            DevSerial = serial;
            DevLink = "USB 调试（adb）";

            var cmd =
                "echo M=$(getprop ro.product.model)" +
                ";echo B=$(getprop ro.product.brand)" +
                ";echo D=$(getprop ro.product.device)" +
                ";echo R=$(getprop ro.build.version.release)" +
                ";echo BUILD=$(getprop ro.build.display.id)" +
                ";echo PATCH=$(getprop ro.build.version.security_patch)" +
                ";echo LOCK=$(getprop ro.boot.flash.locked)" +
                ";echo VBS=$(getprop ro.boot.verifiedbootstate)" +
                ";echo VBSV=$(getprop vendor.boot.verifiedbootstate)" +
                ";echo VBM=$(getprop ro.boot.vbmeta.device_state)" +
                ";echo SBL=$(getprop ro.secureboot.lockstate)" +
                ";echo CMDL=$(cat /proc/cmdline 2>/dev/null)" +
                ";echo SLOT=$(getprop ro.boot.slot_suffix)" +
                ";echo SOCM=$(getprop ro.soc.manufacturer)" +
                ";echo SOC=$(getprop ro.soc.model)" +
                ";echo BOARD=$(getprop ro.product.board)" +
                ";echo HW=$(getprop ro.hardware)" +
                ";echo OPS=$(getprop ro.build.version.oplusrom)" +
                ";echo BDATE=$(getprop ro.build.date)" +
                ";echo ABI=$(getprop ro.product.cpu.abi)" +
                ";echo KERNEL=$(uname -r)" +
                ";echo SEL=$(getenforce 2>/dev/null)" +
                ";echo MEM=$(grep MemTotal /proc/meminfo)" +
                ";echo DF=$(df -h /data | tail -1)" +
                ";echo BAT=$(dumpsys battery 2>/dev/null | grep -E 'level|temperature' | tr '\\n' '|')" +
                ";echo SCR=$(wm size 2>/dev/null | tail -1)" +
                ";echo CORES=$(grep -c ^processor /proc/cpuinfo)";

            var outText = "";
            if (_fs != null) outText = await _fs.RunCommandAsync(cmd, false);

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in outText.Split('\n'))
            {
                var t = line.Trim();
                var i = t.IndexOf('=');
                if (i > 0 && i < t.Length - 1)
                {
                    var k = t[..i].Trim();
                    var v = t[(i + 1)..].Trim();
                    if (k.Length <= 12) map[k] = v;
                }
            }
            string G(string k, string def = "—") => map.TryGetValue(k, out var v) && v.Length > 0 ? v : def;

            DevModel = string.IsNullOrWhiteSpace(model) ? G("M") : model;
            DevAndroid = string.IsNullOrWhiteSpace(androidVer) ? G("R") : androidVer;
            DevCodename = G("D");
            DevVersion = G("BUILD");
            DevKernel = G("KERNEL");

            // 解锁状态不能只信 getprop：隐藏模块会把它伪装成"锁定"，
            // 所以这里存下原始信号，交给 BootloaderState 交叉判定（root 信息后到，还会再判一次）。
            _blFlashLocked = G("LOCK", "");
            _blVerifiedBoot = FirstNonEmpty(G("VBS", ""), G("VBSV", ""));
            _blVbmetaState = G("VBM", "");
            _blSecureLock = G("SBL", "");
            _blCmdline = G("CMDL", "");
            UpdateBootloaderVerdict();

            var slot = G("SLOT", "");
            DevSlot = slot switch { "_a" => "A 槽位", "_b" => "B 槽位", "" => "非 A/B", _ => slot };

            DevCpuVendor = G("SOCM");
            DevCpuModel = G("SOC");
            DevCpuName = G("BOARD") != "—" ? G("BOARD") : G("HW");

            var os = G("OPS", "");
            DevOs = os != "—" ? os : "Android " + DevAndroid;

            var sel = G("SEL", "");
            DevSelinux = sel switch
            {
                "Enforcing" => "严格模式（Enforcing）",
                "Permissive" => "宽松模式（Permissive）",
                "Disabled" => "已关闭（Disabled）",
                _ => sel is "—" ? "—" : sel,
            };

            DevBuildDate = G("BDATE");

            var mem = G("MEM", "");
            DevRam = FormatMem(mem);
            DevRing = G("ABI");

            var scr = G("SCR", "");
            DevScreen = scr.Replace("Physical size:", "").Trim().Length > 0
                ? scr.Replace("Physical size:", "").Trim()
                : "—";

            DevCores = G("CORES") == "—" ? "—" : G("CORES") + " 核";

            var bat = G("BAT", "");
            var lv = System.Text.RegularExpressions.Regex.Match(bat, @"level:\s*(\d+)");
            var tp = System.Text.RegularExpressions.Regex.Match(bat, @"temperature:\s*(\d+)");
            var lvTxt = lv.Success ? lv.Groups[1].Value + "%" : "—";
            BatteryShort = "电量 " + lvTxt;
            DevBattery = "";
            DevBatteryText = BatteryShort + (tp.Success
                ? $"（{(int.Parse(tp.Groups[1].Value, CultureInfo.InvariantCulture) / 10.0):0.#} ℃）"
                : "");

            var df = G("DF", "");
            DevStorage = df == "—" ? "—" : FormatDf(df);

            DevStorageRaw = "";
        }
        catch { }
    }

    [ObservableProperty] private string _devBatteryText = "—";
    [ObservableProperty] private string _devStorageRaw = "";
    [ObservableProperty] private string _devBattery = "";

    private static string FormatMem(string memInfo)
    {
        var m = System.Text.RegularExpressions.Regex.Match(memInfo, @"(\d+)\s*kB");
        if (!m.Success) return "—";
        var kb = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return $"{kb / 1024 / 1024:0.0} GB";
    }

    private static string FormatDf(string df)
    {
        var p = System.Text.RegularExpressions.Regex
            .Split(df.Trim(), @"\s+")
            .Where(x => x.Length > 0).ToArray();
        // /dev/block/xx  211G  3.6G  208G  2%  /data
        if (p.Length >= 5 && p[4].EndsWith("%"))
            return $"{p[2]} / {p[1]}（已用 {p[4]}）";
        return df.Trim();
    }

    private static string Squash(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }

    private static string FirstUsefulLine(string s)
    {
        foreach (var l in s.Split('\n'))
        {
            var t = l.Trim();
            if (t.Length > 0 && !t.StartsWith("[")) return t;
        }
        return "—";
    }

    private static string LastUsefulLine(string s)
    {
        var lines = s.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith("[")).ToList();
        return lines.Count > 0 ? lines[^1] : "—";
    }

    // ---------------- 应用管理 ----------------

    public ObservableCollection<AppItem> Apps { get; } = new();
    [ObservableProperty] private int _appMode;          // 0 = 用户应用，1 = 系统应用
    [ObservableProperty] private string _appsStatus = "";
    private AppService? _apps;
    private IRootProvider? _root;
    private CancellationTokenSource? _appCts;


    [ObservableProperty] private bool _isInstalling;

    private CancellationTokenSource? _installCts;

    /// <summary>中止正在进行的安装（状态栏「取消」按钮）</summary>
    [RelayCommand]
    private void CancelInstall()
    {
        try
        {
            _installCts?.Cancel();
            if (IsInstalling) StatusText = "正在取消安装…";
        }
        catch { }
    }

    /// <summary>安装电脑上的 APK（不需要 root，adb install 即可）</summary>
    public async Task<bool> InstallApkAsync(string localPath, CancellationToken ct = default)
    {
        var serial = SelectedDevice?.Serial ?? _lastSerial;
        if (string.IsNullOrEmpty(serial)) { Toast("未连接设备"); return false; }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _installCts = cts;
        IsInstalling = true;
        try
        {
            StatusText = "正在安装：" + Path.GetFileName(localPath) + "（可点右上角「取消」）";
            // install 单独给 10 分钟：等 su 授权弹窗时 20 秒太短，30 分钟又太久
            var r = await _runner.RunAsync(new[] { "-s", serial, "install", "-r", localPath },
                _runner.InstallTimeout, cts.Token);
            if (cts.IsCancellationRequested)
            {
                StatusText = "已取消安装：" + Path.GetFileName(localPath);
                return false;
            }
            var ok = r.All.Contains("Success", StringComparison.OrdinalIgnoreCase);
            Toast(ok ? "安装成功：" + Path.GetFileName(localPath) : "安装失败：" + FirstUsefulLine(r.All));
            if (ok) await LoadAppsAsync();
            return ok;
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消安装：" + Path.GetFileName(localPath);
            return false;
        }
        catch (Exception ex)
        {
            Toast("安装失败：" + ex.Message);
            return false;
        }
        finally
        {
            IsInstalling = false;
            if (ReferenceEquals(_installCts, cts)) _installCts = null;
        }
    }

    /// <summary>
    /// 清理调试痕迹。
    /// deep = true 时：额外删 USB 授权，并在最后关闭 USB 调试与开发者选项（连接会断开）。
    /// 原则：只删文件、保留目录；系统受保护的路径一律不碰。
    /// </summary>
    public async Task<List<(string Label, string Value)>> CleanTracesAsync(bool deep)
    {
        var rows = new List<(string, string)>();
        if (_fs == null) { rows.Add(("状态", "未连接设备")); return rows; }

        async Task<(bool ok, string note)> Run(string cmd)
        {
            try
            {
                var r = await _fs.RunCommandAsync(cmd + " && echo DF_OK", true);
                return (r.Contains("DF_OK"), "");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // ① Root 管理器日志（KernelSU / Magisk）
        var raw = await _fs.RunCommandAsync(
            $"du -sk {DevicePaths.KsuDir}/log 2>/dev/null | cut -f1; " +
            $"du -sk {DevicePaths.MagiskDir}.log 2>/dev/null | cut -f1", true);
        long before = 0;
        foreach (var line in raw.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"(?m)^\s*(\d+)\s*$");
            if (m.Success) before += long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        var (ok1, _) = await Run(
            $"rm -f {DevicePaths.KsuDir}/log/*.log {DevicePaths.KsuDir}/log/*.old.log " +
            $"{DevicePaths.KsuDir}/log/*.gz {DevicePaths.MagiskDir}.log " +
            $"{DevicePaths.MagiskDir}/*.log 2>/dev/null");
        rows.Add(("Root 日志（KSU / Magisk）", ok1 ? (before > 0 ? $"已清空（{before / 1024} MB）" : "已清空") : "失败"));

        // ② logcat 全部分区 + 持久化日志
        var (ok2, _) = await Run("logcat -b all -c 2>/dev/null; setprop persist.logd.logpersistd clear 2>/dev/null");
        rows.Add(("logcat 缓冲", ok2 ? "已清空（main/system/crash/events）" : "失败"));

        // ③ 临时文件（只删我们的命名规则）
        var (ok3, _) = await Run(
            $"rm -rf {DevicePaths.LocalTmp}/.dashfile* {DevicePaths.LocalTmp}/.df_* " +
            $"{DevicePaths.LocalTmp}/dashfile_* 2>/dev/null");
        rows.Add(("临时文件", ok3 ? "已清理" : "失败"));

        // ④ 内核环形缓冲
        var (ok4, _) = await Run("dmesg -c >/dev/null 2>&1");
        rows.Add(("dmesg", ok4 ? "已清空" : "系统拒绝（部分 ROM 限制）"));

        // ⑤ 崩溃转储：只删文件，保留目录
        var (ok5, _) = await Run($"rm -f {DevicePaths.Dropbox}/* 2>/dev/null");
        rows.Add(("dropbox 转储", ok5 ? "文件已清空（目录保留）" : "系统拒绝"));

        // ⑥ 原生崩溃堆栈：只删文件，保留目录
        var (ok6, _) = await Run($"rm -f {DevicePaths.Tombstones}/* 2>/dev/null");
        rows.Add(("tombstone 堆栈", ok6 ? "文件已清空（目录保留）" : "系统拒绝"));

        if (deep)
        {
            // ⑦ USB 调试授权
            var (ok7, _) = await Run(
                $"rm -f {DevicePaths.AdbKeys} {DevicePaths.AdbTempKeys} 2>/dev/null");
            rows.Add(("USB 调试授权", ok7 ? "已删除 — 下次连接需重新授权" : "失败"));

            // ⑧ 电池统计（含 USB 连接事件）
            var (ok8, _) = await Run("dumpsys batterystats --reset >/dev/null 2>&1");
            rows.Add(("电池统计", ok8 ? "已重置 — 电池页会显示归零" : "系统拒绝"));

            // ⑨ 最后一步：关开发者选项 → 关 USB 调试（顺序不能反，adb 一关就断线）
            var (ok9, _) = await Run(
                "settings put global development_settings_enabled 0; settings put global adb_enabled 0");
            rows.Add(("USB 调试开关", ok9 ? "已关闭（含开发者选项）— 连接已断开" : "失败"));
        }
        else
        {
            rows.Add(("USB 调试授权", "已保留 — 深度清理才会删除"));
            rows.Add(("电池统计", "已保留 — 深度清理才会重置"));
            rows.Add(("USB 调试开关", "已保留"));
        }

        rows.Add(("说明", deep ? "如需再次使用，请在手机上重新打开开发者选项与 USB 调试" : "设备端不保留本工具产生的文件"));
        return rows;
    }

    /// <summary>软重启：只重启 Android 用户态，约 30 秒，授权表会重新加载</summary>
    public async Task SoftRebootAsync()
    {
        if (_fs == null) { Toast("未连接设备"); return; }
        if (_root == null) { Toast("未检测到 Root 实现，无法执行软重启"); return; }
        try
        {
            Toast("正在软重启，设备会断开约 10~30 秒…");
            var result = await _root.SoftRebootAsync();
            Toast(result.Message);
        }
        catch (Exception ex) { Toast("软重启失败：" + ex.Message); }
    }

    /// <summary>切换某应用的 root 授权（直接改写授权表，不经管理器）</summary>
    public async Task ToggleRootAsync(AppItem item, bool allowed)
    {
        if (_fs == null) return;
        if (_root == null) { Toast("未检测到 Root 实现，操作中止"); item.IsGranted = !allowed; return; }
        if (!_root.CanManageGrants)
        {
            Toast($"{_root.DisplayName} 的授权表暂不支持直接修改，请用管理器 App");
            item.IsGranted = !allowed;
            return;
        }

        var uidRaw = await _fs.RunCommandAsync(
            $"stat -c %u {Sh.Q(DevicePaths.UserData(item.Package))} 2>/dev/null", true);
        var mm = System.Text.RegularExpressions.Regex.Match(uidRaw, @"(?m)^\s*(\d+)\s*$");
        var uid = mm.Success ? int.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        if (uid == 0) { Toast("取不到 UID，操作中止"); item.IsGranted = !allowed; return; }

        var result = await _root.SetGrantAsync(item.Package, uid, allowed);
        item.IsGranted = result.Ok ? allowed : !allowed;
        Toast(result.Ok
            ? $"{item.Label}：{(allowed ? "已授权" : "已取消")}（{result.Message}）"
            : result.Message);
        LogLine($"SU {item.Package} => {allowed} : {result.Message}");

        if (result.Ok)
        {
            // 授权表已变，页面顶部的"N 个已授权"和仪表盘计数要一起更新，否则要刷新才变
            var total = Apps.Count;
            if (AppMode == 2 && total > 0)
                AppsStatus = $"{Apps.Count(x => x.IsGranted)} 个已授权 · 共 {total} 个应用";
            await RefreshGrantCountAsync();
        }
    }

    /// <summary>只重读授权表并更新仪表盘计数（比 LoadRootInfoAsync 轻，Toggler 用）</summary>
    private async Task RefreshGrantCountAsync()
    {
        if (_root == null || !_root.CanReadGrants) return;
        try
        {
            var grants = await _root.ReadGrantsAsync();
            SuperuserCount = _root.CanReadGrants ? grants.Count(g => g.Allowed).ToString() : "—";
        }
        catch (Exception ex) { AppLog.Add("刷新授权计数失败：" + ex.Message); }
    }

    /// <summary>应用详情</summary>
    public async Task<List<(string Label, string Value)>> GetAppDetailsAsync(AppItem item)
    {
        if (_fs == null || _apps == null) return new List<(string, string)> { ("提示", "未连接设备") };
        var rows = await _apps.GetDetailsAsync(item.Package, item.ApkPath);
        if (rows.Count == 0) rows.Add(("提示", "读取失败"));
        return rows;
    }

    /// <summary>卸载应用（用户应用用 pm uninstall，系统应用用 --user 0）</summary>
    public async Task<bool> UninstallAppAsync(AppItem item)
    {
        if (_fs == null) return false;
        try
        {
            var cmd = item.IsSystem
                ? $"pm uninstall --user 0 {Sh.Q(item.Package)}"
                : $"pm uninstall {Sh.Q(item.Package)}";
            // 有 root 走 root，没有就用 shell（adb shell 本身有安装/卸载权限）
            var r = await _fs.RunCommandAsync(cmd, _fs.UseRoot);
            var ok = r.Contains("Success", StringComparison.OrdinalIgnoreCase);

            if (!ok && item.IsSystem)
            {
                await _fs.RunCommandAsync($"pm disable-user --user 0 {Sh.Q(item.Package)}", _fs.UseRoot);
                Toast("系统应用已停用（如需恢复请到系统设置）");
            }
            else
            {
                Toast(ok ? "已卸载：" + item.Label : "卸载失败：" + FirstUsefulLine(r));
            }

            await LoadAppsAsync();
            return ok;
        }
        catch (Exception ex)
        {
            Toast("卸载失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>把 APK 提取到电脑</summary>
    public async Task<bool> ExtractApkToPcAsync(AppItem item, string localPath)
    {
        if (_fs == null || string.IsNullOrEmpty(item.ApkPath)) return false;
        try
        {
            StatusText = "正在导出安装包…";
            await _fs.DownloadAsync(item.ApkPath, localPath);
            var name = Path.GetFileName(localPath);
            Toast($"已保存：{name}");
            return true;
        }
        catch (Exception ex)
        {
            Toast("导出失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>把 APK 复制到手机上的某个目录（用户明确触发的写操作）</summary>
    public async Task<bool> ExtractApkToPhoneAsync(AppItem item, string dir)
    {
        if (_fs == null || string.IsNullOrEmpty(item.ApkPath)) return false;
        try
        {
            var name = (item.Label.Length > 0 ? item.Label : item.Package) + ".apk";
            name = name.Replace("/", "_");
            var dest = Sh.Join(dir, name);
            var r = await _fs.RunCommandAsync($"cp -f {Sh.Q(item.ApkPath)} {Sh.Q(dest)}", _fs.UseRoot);
            var ok = !r.Contains("denied", StringComparison.OrdinalIgnoreCase) && !r.Contains("No such", StringComparison.OrdinalIgnoreCase);
            Toast(ok ? $"已保存到 {dest}" : "保存失败：" + FirstUsefulLine(r));
            return ok;
        }
        catch (Exception ex)
        {
            Toast("保存失败：" + ex.Message);
            return false;
        }
    }

    private readonly Dictionary<string, List<string>> _dirCache = new(StringComparer.Ordinal);
    private static bool PerfOn => AppEnvironment.Perf;

    /// <summary>性能埋点（DASHFILE_PERF=1 时写 Log\perf.log，平时完全不走）</summary>
    internal static void Perf(string msg)
    {
        if (!PerfOn) return;
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Log");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "perf.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>探针：DASHFILE_PERF=1 时自动走一串目录，把每一段的耗时写进日志</summary>
    private async Task PerfProbeAsync()
    {
        if (!PerfOn) return;
        await Task.Delay(3000);
        foreach (var path in new[]
                 {
                     DevicePaths.Sdcard, DevicePaths.Download, DevicePaths.Dcim, DevicePaths.Sdcard,
                     DevicePaths.AdbData, DevicePaths.Modules, DevicePaths.Sdcard, DevicePaths.SdcardAndroid
                 })
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await NavigateAsync(path);
            Perf($"PROBE {path,-24} total={sw.ElapsedMilliseconds}ms");
            await Task.Delay(400);
        }
        Perf("PROBE done");
    }

    private readonly Dictionary<string, (DateTime At, List<PosixEntry> Items)> _listCache = new(StringComparer.Ordinal);
    private const int ListCacheMs = 2500;

    // 预取：进入某目录后，后台把它的子目录也列好，点进去就是 0ms（随后再静默校准）
    private readonly Dictionary<string, (DateTime At, List<PosixEntry> Items)> _prefetch = new(StringComparer.Ordinal);
    private CancellationTokenSource? _prefetchCts;
    private const int PrefetchTtlSeconds = 20;
    private const int PrefetchCount = 8;
    private CancellationTokenSource? _enrichCts;

    /// <summary>后台逐个预热子目录（用户停在某目录时才开始，一旦导航立刻取消）</summary>
    private async Task PrefetchAsync(IReadOnlyList<PosixEntry> entries)
    {
        if (_fs == null) return;
        _prefetchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _prefetchCts = cts;
        try
        {
            var dirs = entries.Where(e => e.IsDir).Take(PrefetchCount).ToList();
            var n = 0;
            foreach (var d in dirs)
            {
                // 前 3 个立刻做（点得最快的往往就是这几个），后面的慢慢来
                if (n++ >= 3) await Task.Delay(140, cts.Token);
                if (_prefetch.ContainsKey(d.FullPath)) continue;
                var sub = await _fs.ListAsync(d.FullPath, cts.Token);
                _prefetch[d.FullPath] = (DateTime.Now, sub);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            if (ReferenceEquals(_prefetchCts, cts)) _prefetchCts = null;
        }
    }

    /// <summary>预取命中的目录：后台再取一次真实内容，和当前显示不一致就静默替换（只换一次，不闪）</summary>
    private async Task RevalidateAsync(string path)
    {
        if (_fs == null) return;
        try
        {
            var fresh = await Task.Run(() => _fs.ListAsync(path));
            _listCache[path] = (DateTime.Now, fresh);
            _prefetch[path] = (DateTime.Now, fresh);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!string.Equals(CurrentPath, path, StringComparison.Ordinal)) return;
                if (fresh.Count == _entriesRaw.Count)
                {
                    var same = true;
                    for (var i = 0; i < fresh.Count && same; i++)
                    {
                        var a = fresh[i]; var b = _entriesRaw[i];
                        same = a.FullPath == b.FullPath && a.Size == b.Size && a.Modified == b.Modified;
                    }
                    if (same) return;
                }
                _entriesRaw = fresh.ToList();
                ApplySort();
            });
        }
        catch { }
    }

    /// <summary>任何"写"操作后清掉预取缓存，避免看到过期目录</summary>
    private void InvalidatePrefetch()
    {
        _prefetch.Clear();
        _treeCache.Clear();
        _listCache.Clear();      // 以前漏了列表缓存：写操作后 2.5 秒内还能看到旧内容
        _hasKids.Clear();
        _enriched.Clear();
        _treeLoads.Clear();      // 结构变了，正在跑的树加载结果也不再可信
    }

    // 目录树专用缓存：比列表缓存活得久（树可能几分钟才展开一次），写操作时统一作废
    private readonly Dictionary<string, (DateTime At, List<PosixEntry> Items)> _treeCache = new(StringComparer.Ordinal);
    private const int TreeCacheMs = 45_000;

    /// <summary>只列子目录（树专用）：比完整 ls -l -Z 轻得多，且不用解析权限/日期</summary>
    private async Task<List<PosixEntry>> GetTreeListingAsync(string path, CancellationToken ct = default)
    {
        if (_treeCache.TryGetValue(path, out var hit) &&
            (DateTime.Now - hit.At).TotalMilliseconds < TreeCacheMs)
            return hit.Items;

        var list = await _fs!.ListDirsAsync(path, ct);
        _treeCache[path] = (DateTime.Now, list);
        return list;
    }

    /// <summary>
    /// 列目录（带 2.5s 缓存）。切目录、树懒加载、树补全都走这里，
    /// 同一个目录在短时间内只会真正跑一次 adb。
    /// </summary>
    private async Task<List<PosixEntry>> GetListingAsync(string path, CancellationToken ct = default)
    {
        if (_listCache.TryGetValue(path, out var hit) &&
            (DateTime.Now - hit.At).TotalMilliseconds < ListCacheMs)
            return hit.Items;

        var list = await _fs!.ListAsync(path, ct);
        _listCache[path] = (DateTime.Now, list);
        return list;
    }

    /// <summary>列出某个目录下的子目录（一条命令搞定，避免逐条 adb 往返）</summary>
    public async Task<List<string>> ListSubDirsAsync(string dir, bool force = false)
    {
        if (!force && _dirCache.TryGetValue(dir, out var cached)) return cached;

        var result = new List<string>();
        if (_fs == null) return result;

        try
        {
            // ls -1p 会在目录名后加 "/"；比 find -type d 在 /sdcard（FUSE）上可靠得多
            var raw = await _fs.RunCommandAsync($"ls -1p {Sh.Q(dir)} 2>/dev/null", false);

            foreach (var line in raw.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("[")) continue;
                if (!t.EndsWith("/", StringComparison.Ordinal)) continue;   // 只要目录
                var name = t.TrimEnd('/');
                if (name.Length > 0) result.Add(name);
            }

            if (result.Count == 0)
            {
                // 兜底：有些环境 ls -p 也不给斜杠，用 find 再试一次
                var alt = await _fs.RunCommandAsync(
                    $"find {Sh.Q(dir)} -mindepth 1 -maxdepth 1 -type d 2>/dev/null", false);
                foreach (var line in alt.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("[")) continue;
                    var name = t.TrimEnd('/').Split('/').Last();
                    if (name.Length > 0) result.Add(name);
                }
            }
        }
        catch { }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        _dirCache[dir] = result;
        return result;
    }

    public bool IsUserMode => AppMode == 0;
    public bool IsSysMode => AppMode == 1;
    public bool IsSuperMode => AppMode == 2;

    partial void OnAppModeChanged(int value)
    {
        OnPropertyChanged(nameof(IsUserMode));
        OnPropertyChanged(nameof(IsSysMode));
        OnPropertyChanged(nameof(IsSuperMode));
        LoadAppsAsync().Safe("读取应用", m => StatusText = m);
    }

    public async Task LoadAppsAsync()
    {
        if (_fs == null) { AppsStatus = "未连接设备"; return; }

        _appCts?.Cancel();
        _appCts = new CancellationTokenSource();
        var ct = _appCts.Token;

        try
        {
            Apps.Clear();
            AppsStatus = "正在读取应用列表…";
            _apps ??= new AppService(_fs);

            List<AppItem> list;
            var tableUnsupported = false;
            var readOnly = false;

            if (AppMode == 2)
            {
                // 超级用户：只列用户应用 + 固定的 shell（adb 调试用的就是它）
                // 统一走 provider：KernelSU 读二进制授权表，Magisk 读 magisk.db，
                //   不能读的实现返回空表并标记 tableUnsupported，UI 会提示"该实现不支持"。
                var grants = _root != null && _root.CanReadGrants
                    ? await _root.ReadGrantsAsync(ct)
                    : new List<RootGrant>();
                tableUnsupported = _root == null || !_root.CanReadGrants;
                readOnly = _root != null && _root.CanReadGrants && !_root.CanManageGrants;
                var granted = grants.ToDictionary(g => g.Package, g => g.Allowed);

                list = await Task.Run(() => _apps.ListAsync(false, ct), ct);

                var shell = list.FirstOrDefault(a => a.Package == AppInfo.ShellPackage);
                if (shell == null)
                    list.Insert(0, new AppItem
                    {
                        Package = AppInfo.ShellPackage,
                        Label = "Shell",
                        IsSystem = true,
                        ApkPath = "",
                    });

                foreach (var a in list)
                {
                    a.ShowActions = false;
                    a.ShowSwitch = true;
                    a.CanToggleRoot = _root?.CanManageGrants ?? false;
                    a.IsGranted = granted.TryGetValue(a.Package, out var ok) && ok;
                }
            }
            else
            {
                list = await Task.Run(() => _apps.ListAsync(AppMode == 1, ct), ct);
            }

            if (ct.IsCancellationRequested) return;
            foreach (var a in list) Apps.Add(a);

            var total = list.Count;
            AppsStatus = AppMode == 2
                ? (tableUnsupported
                    ? "当前 Root 实现的授权表不受支持"
                    : readOnly
                        ? $"{list.Count(x => x.IsGranted)} 个已授权 · 共 {total} 个应用 · 授权表只读（请用管理器 App 修改）"
                        : $"{list.Count(x => x.IsGranted)} 个已授权 · 共 {total} 个应用 · 正在读取图标…")
                : $"{total} 个应用 · 正在读取版本与图标…";
            var done = 0;
            // 不要 using：下面的图标任务是 fire-and-forget（_ = Task.Run），方法返回时它们还在跑。
            //   用 using 会在方法结束时 Dispose，任务里的 finally 再 Release 就抛 ObjectDisposedException——
            //   而 finally 抛出的异常不会被 catch 接住，任务变成 Unobserved，最后在终结器线程炸掉进程。
            var sem = new SemaphoreSlim(8);
            var loadWatch = System.Diagnostics.Stopwatch.StartNew();

            // 一次 dumpsys 拿到所有 versionName，避免 370 个应用各跑一次 adb（约省 30 秒）。
            //   包很少时单次查询更划算；解析为空则退回逐包查询。
            Dictionary<string, string>? versionMap = null;
            if (list.Count >= 20)
            {
                var versionWatch = System.Diagnostics.Stopwatch.StartNew();
                versionMap = await _apps.GetVersionsAsync(list.Select(a => a.Package).ToList(), ct);
                Perf($"app versions batch -> {versionMap.Count} in {versionWatch.ElapsedMilliseconds}ms");
                if (versionMap.Count == 0) versionMap = null;
            }

            foreach (var item in list)
            {
                if (ct.IsCancellationRequested) break;
                await sem.WaitAsync(ct);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var v = versionMap != null
                            ? (versionMap.TryGetValue(item.Package, out var known) ? known : "")
                            : await _apps.GetVersionAsync(item.Package, ct);
                        var label = item.Package == AppInfo.ShellPackage
                            ? "Shell（adb 调试）"
                            : await _apps.GetLabelAsync(item.Package, item.ApkPath, ct);
                        var (icon, exact) = await _apps.GetIconAsync(item.Package, item.ApkPath, ct);
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (!string.IsNullOrEmpty(v)) item.Version = "v" + v;
                            if (!string.IsNullOrEmpty(label)) item.Label = label;
                            if (icon != null) { item.Icon = icon; item.IconUncertain = !exact; }
                            done++;
                            if (done == total)
                                Perf($"apps detail done -> {total} in {loadWatch.ElapsedMilliseconds}ms");
                            if (done % 3 == 0 || done == total)
                                AppsStatus = AppMode == 2
                                    ? (readOnly
                                        ? $"{list.Count(x => x.IsGranted)} 个已授权 · 共 {total} 个应用 · 授权表只读"
                                        : $"{list.Count(x => x.IsGranted)} 个已授权 · 共 {total} 个应用")
                                    : $"{total} 个应用 · 已读取 {done}";
                        });
                    }
                    catch { }
                    finally { try { sem.Release(); } catch (ObjectDisposedException) { } }
                }, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppsStatus = "读取失败：" + ex.Message; }
    }

    // 插件相关字段 / 属性 / 命令已拆到 MainViewModel.Plugins.cs

    // ---------------- 设置 / 主题 ----------------

    public ObservableCollection<ThemeOption> Themes { get; } = new();
    public ObservableCollection<StyleOption> Styles { get; } = new();
    [ObservableProperty] private bool _isGlassEnabled;

    partial void OnIsGlassEnabledChanged(bool value)
    {
        ThemeService.ApplyGlass(value, StyleKey, AccentHex);
        AppSettings.Current.GlassEnabled = value;
        AppSettings.Save();
    }

    [ObservableProperty] private bool _isMaterialEnabled = true;

    partial void OnIsMaterialEnabledChanged(bool value)
    {
        ThemeService.ApplyMaterial(value, StyleKey, AccentHex);
        AppSettings.Current.MaterialEnabled = value;
        AppSettings.Save();
    }

    [ObservableProperty] private bool _isLightMode;

    partial void OnIsLightModeChanged(bool value)
    {
        ThemeService.ApplyAppearance(value ? "light" : "dark", StyleKey, AccentHex);
        AppSettings.Current.Appearance = value ? "light" : "dark";
        AppSettings.Save();
    }

    [ObservableProperty] private string _styleKey = "m3";

    [ObservableProperty] private string _accentHex = "#A78BFA";
    [ObservableProperty] private bool _isDragging;
    [ObservableProperty] private string _dragHint = "";
    [ObservableProperty] private bool _isMultiSelect;
    [ObservableProperty] private string _multiSelectHint = "";
    public string AppVersion => AppInfo.Version;
    public string Signature => AppInfo.Signature;
    public string WindowTitle => AppInfo.Product;

    [RelayCommand]
    private void ApplyTheme(ThemeOption? t)
    {
        if (t == null) return;
        AccentHex = t.Hex;
        try { ThemeService.ApplyStyle(StyleKey, t.Hex); } catch { }
        AppSettings.Current.AccentHex = t.Hex;
        AppSettings.Save();
        foreach (var x in Themes) x.IsSelected = ReferenceEquals(x, t);
        StatusText = "主题色已切换：" + t.Name;
    }

    /// <summary>把设备上的文件导出到电脑上的某个文件夹（拖到窗口外时用）</summary>
    public async Task ExportToAsync(IReadOnlyList<PosixEntry> items, string localDir)
    {
        var fs=_fs;if(fs==null||items.Count==0||string.IsNullOrWhiteSpace(localDir))return;
        Directory.CreateDirectory(localDir);var completed=0;
        foreach(var item in items)
        {
            if(!ReferenceEquals(fs,_fs)){LogLine("EXPORT 设备已切换，停止后续导出");break;}
            try
            {
                if(Path.GetFileName(item.Name)!=item.Name||item.Name is "." or "..")throw new IOException("文件名不适用于 Windows 导出");
                if(item.IsDir)
                {
                    var remoteTar=DevicePaths.TempFile("export",".tar.gz");
                    try{await fs.TarGzAsync(item.FullPath,remoteTar);await fs.DownloadAsync(remoteTar,Path.Combine(localDir,item.Name+".tar.gz"));}
                    finally{await fs.DeleteAsync(new[]{remoteTar},CancellationToken.None);}
                }
                else await fs.DownloadAsync(item.FullPath,Path.Combine(localDir,item.Name));
                completed++;
            }
            catch(OperationCanceledException){StatusText=$"导出已取消，完成 {completed}/{items.Count} 项";return;}
            catch(Exception ex){LogLine("EXPORT "+ex.Message);}
        }
        StatusText=$"已导出 {completed}/{items.Count} 项到 {localDir}";Toast(StatusText);
    }

    [RelayCommand]
    private void ApplyStyleOption(StyleOption? option)
    {
        if (option == null) return;
        StyleKey = option.Key;
        try { ThemeService.ApplyStyle(option.Key, AccentHex); } catch { }
        AppSettings.Current.StyleKey = option.Key;
        AppSettings.Save();
        foreach (var s in Styles) s.IsSelected = ReferenceEquals(s, option);
        Toast("界面风格已切换：" + option.Name);
    }

    /// <summary>把若干远程路径移动到目标目录</summary>
    public async Task<int> MoveAsync(IReadOnlyList<string> srcPaths, string targetDir)
    {
        InvalidatePrefetch();
        if (_fs == null || srcPaths.Count == 0 || string.IsNullOrWhiteSpace(targetDir)) return 0;

        var fs=_fs;
        var target = targetDir.TrimEnd('/');
        if (target.Length == 0) target = "/";

        var moved = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var raw in srcPaths)
        {
            var src = raw.TrimEnd('/');
            if (src.Length == 0) continue;
            var name = src.Split('/').Last();
            var parent = Sh.Parent(src).TrimEnd('/');
            if (parent.Length == 0) parent = "/";
            if (string.Equals(parent, target, StringComparison.Ordinal)) { skipped++; continue; }
            if (string.Equals(src, target, StringComparison.Ordinal)) { skipped++; continue; }
            if (target.StartsWith(src + "/", StringComparison.Ordinal)) { skipped++; continue; }

            var dst = (target == "/" ? "" : target) + "/" + name;
            // 用"落地后确实存在"作为成功标志：只靠 stderr 关键字会被 locale / 静默失败骗过
            var r = await fs.RunCommandAsync(
                $"mv -f -- {Sh.Q(src)} {Sh.Q(dst)} && [ -e {Sh.Q(dst)} ] && echo DF_OK", true);
            if (!r.Contains("DF_OK", StringComparison.Ordinal))
            {
                failed++;
                LogLine("MV FAIL " + src + " -> " + dst + " : " + r.Trim());
                continue;
            }
            moved++;
            LogLine($"MV {src} -> {dst}");
        }

        var failText = failed > 0 ? $"，{failed} 项失败（详见日志）" : "";
        StatusText = skipped > 0
            ? $"已移动 {moved} 项（{skipped} 项已在目标目录或无效，跳过）" + failText
            : $"已移动 {moved} 项到 {target}" + failText;
        if (failed > 0) AppLog.Add("移动失败 " + failed + " 项 → " + target);
        if(ReferenceEquals(fs,_fs)) await NavigateAsync(CurrentPath);
        return moved;
    }

    // ---------------- 路径面包屑 ----------------

    private void BuildCrumbs(string path)
    {
        Crumbs.Clear();
        Crumbs.Add(new PathNode { Name = "根目录", FullPath = "/", Depth = 0 });

        var acc = "";
        var depth = 1;
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            acc += "/" + seg;
            Crumbs.Add(new PathNode { Name = seg, FullPath = acc, Depth = depth++ });
        }
    }

    [RelayCommand] private void BeginPathEdit() => IsPathEditing = true;
    [RelayCommand] private void EndPathEdit() => IsPathEditing = false;

    // ---------------- 排序 / 提示 ----------------

    public string SortColumn => _sortColumn;
    public bool SortAsc => _sortAsc;

    /// <summary>点表头排序：同一列再点一次切换升降序</summary>
    public void SortBy(string column)
    {
        if (string.Equals(_sortColumn, column, StringComparison.Ordinal)) _sortAsc = !_sortAsc;
        else { _sortColumn = column; _sortAsc = true; }

        ApplySort();
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortAsc));
        Toast($"按{ ColumnName(_sortColumn) }{ (_sortAsc ? "升序" : "降序") }排序");
    }

    private static string ColumnName(string c) => c switch
    {
        "date" => "修改日期",
        "type" => "类型",
        "size" => "大小",
        _ => "名称"
    };

    partial void OnFilterTextChanged(string value) => ApplySort();

    private void ApplySort()
    {
        IEnumerable<PosixEntry> q = string.IsNullOrWhiteSpace(FilterText)
            ? _entriesRaw
            : _entriesRaw.Where(x => x.Name.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        q = _sortColumn switch
        {
            "date" => _sortAsc ? q.OrderBy(x => x.Modified) : q.OrderByDescending(x => x.Modified),
            "type" => _sortAsc ? q.OrderBy(x => x.Kind, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                               : q.OrderByDescending(x => x.Kind, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            "size" => _sortAsc ? q.OrderBy(x => x.Size) : q.OrderByDescending(x => x.Size),
            _ => _sortAsc ? q.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                          : q.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
        };

        var sorted = q.ToList();
        Entries.ReplaceAll(sorted);        // 一次 Reset，避免逐条 Add 触发 N 次布局
        IsEmpty = sorted.Count == 0;
    }

    [ObservableProperty] private bool _isToastVisible;
    [ObservableProperty] private string _toastText = "";
    private int _toastStamp;

    /// <summary>右下角轻提示</summary>
    public void Toast(string message)
    {
        ToastText = message;
        IsToastVisible = true;
        var stamp = ++_toastStamp;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2400);
            if (stamp == _toastStamp)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => IsToastVisible = false);
        });
    }

    // ---------------- 浏览 ----------------

    public async Task NavigateAsync(string path)
    {
        if (_fs == null) return;
        // 导航代际：快速连点目录时，旧请求可能在新的之后才返回，
        //   用它自己的 token 判断是否已被取代，避免旧目录把新目录覆盖掉。
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _cts, cts);
        previous?.Cancel();

        try
        {
            IsBusy = true;

            // 让路：正在跑的"目录树补全"和预取会和新目录的 ls 抢同一个 adb 进程
            _enrichCts?.Cancel();
            _prefetchCts?.Cancel();

            var swNav = System.Diagnostics.Stopwatch.StartNew();
            var fromPrefetch = false;
            List<PosixEntry> list;

            if (!_listCache.ContainsKey(path) &&
                _prefetch.TryGetValue(path, out var pf) &&
                (DateTime.Now - pf.At).TotalSeconds < PrefetchTtlSeconds)
            {
                list = pf.Items;         // 命中预取：立刻上屏，不等 adb
                fromPrefetch = true;
            }
            else
            {
                list = await GetListingAsync(path, cts.Token);
            }
            if (!ReferenceEquals(cts, _cts)) return;   // 已被更新的导航取代
            var tLs = swNav.ElapsedMilliseconds;
            if (fromPrefetch) Perf($"  (prefetch hit, saved ~{tLs}ms)");

            _entriesRaw = list.ToList();
            ApplySort();      // 内部会填充 Entries（含排序/筛选），不重复加两遍
            var tSort = swNav.ElapsedMilliseconds;

            CurrentPath = path;
            BuildCrumbs(path);
            IsEmpty = list.Count == 0;
            Perf($"nav {path,-24} ls={tLs}ms sort={tSort - tLs}ms items={list.Count}");
            StatusText = $"共 {list.Count} 个项目    {path}";

            // 树同步放到后台，别挡住列表刷新
            var snapshot = list;
            SyncTreeAsync(path, snapshot).Safe("同步目录树");

            if (fromPrefetch) RevalidateAsync(path).Safe("校准目录");
            PrefetchAsync(snapshot).Safe("预取目录");
        }
        catch (OperationCanceledException) { /* 被更新的一次导航取消，正常 */ }
        catch (Exception ex)
        {
            if (!ReferenceEquals(cts, _cts)) return;
            StatusText = "无法打开该目录：" + ex.Message;
            LogLine("LIST " + path + " => " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(cts, _cts)) IsBusy = false;
        }
    }

    [RelayCommand] public Task RefreshAsync()
    {
        // 导航是"预取优先"的：只删 _listCache 时，_prefetch 里 20 秒内的旧内容仍然生效，
        //   点刷新等于没刷新。三个缓存都要按当前路径作废。
        _listCache.Remove(CurrentPath);
        _prefetch.Remove(CurrentPath);
        _treeCache.Remove(CurrentPath);
        return NavigateAsync(CurrentPath);
    }

    [RelayCommand] public Task GoUpAsync() => NavigateAsync(Sh.Parent(CurrentPath));
    public Task GoAsync() => NavigateAsync(CurrentPath.Trim());

    public async Task OpenEntryAsync(PosixEntry? e)
    {
        if (e == null) return;
        if (e.IsDir || e.IsLink) await NavigateAsync(e.FullPath);
        else StatusText = "这是文件，双击不会进入；可用「编辑」或「下载」";
    }

    // ---------------- 左侧目录树 ----------------

    /// <summary>进入某个路径时：把祖先链补齐并展开，选中当前目录（其它分支不动）</summary>
    private async Task SyncTreeAsync(string path, IReadOnlyList<PosixEntry>? entries = null)
    {
        var swTree = System.Diagnostics.Stopwatch.StartNew();
        _rootNode ??= GetNode("/", "/");

        // 先只把"节点链"搭好，再一次性并发补齐所有未加载的层级。
        // 以前是一级一级 await：路径深 3 级就是 3 次串行 ls（每次 150~250ms），
        //   切目录时看起来就是"树要顿半秒"。并发之后总耗时≈最慢的那一次。
        var chain = new List<DirNode> { _rootNode };
        var acc = "";
        var parent = _rootNode;

        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            acc += "/" + seg;
            var node = GetNode(acc, seg);
            if (!parent.Children.Contains(node)) parent.Children.Add(node);
            chain.Add(node);
            parent = node;
        }

        var loads = new List<Task>(chain.Count);
        foreach (var n in chain)
            if (!n.IsLoaded) loads.Add(LoadChildrenAsync(n));
        if (loads.Count > 1) await Task.WhenAll(loads);
        else if (loads.Count == 1) await loads[0];

        foreach (var n in chain) n.IsExpanded = true;
        if (!parent.IsLoaded) await LoadChildrenAsync(parent, entries);
        QueueEnrich(parent);

        // 重建现在很便宜（增量 Patch，内容没变时一条事件都不发），
        //   所以不再区分 "结构变了 / 只是高亮变了"，每次走同一条路，少一堆状态判断。
        RebuildRows();
        Perf($"tree {path,-23} {swTree.ElapsedMilliseconds}ms rows={TreeRows.Count}");
    }

    private DirNode GetNode(string path, string name)
    {
        if (_nodes.TryGetValue(path, out var n)) return n;
        n = new DirNode { Name = name, FullPath = path };
        _nodes[path] = n;
        return n;
    }

    private readonly HashSet<string> _hasKids = new(StringComparer.Ordinal);

    /// <summary>已经补过箭头的节点（每次刷新目录树时清空）</summary>
    private readonly HashSet<string> _enriched = new(StringComparer.Ordinal);

    /// <summary>正在加载的节点：连续快速导航时复用同一次 ls，不重复发命令</summary>
    private readonly Dictionary<string, Task> _treeLoads = new(StringComparer.Ordinal);

    private Task LoadChildrenAsync(DirNode node, IReadOnlyList<PosixEntry>? entries = null)
    {
        if (_fs == null || node.IsLoaded) return Task.CompletedTask;
        if (_treeLoads.TryGetValue(node.FullPath, out var running)) return running;

        var task = LoadChildrenCoreAsync(node, entries);
        // entries 分支是同步跑完的：LoadChildrenCoreAsync 的 finally 里 Remove 已经执行过，
        //   这里再把它塞进字典，就留下一个"永远已完成"的僵尸任务。之后刷新目录树
        //   （IsLoaded 全置 false）会命中这条僵尸直接 return → 该目录的子树永久空、再也加载不出来。
        if (!task.IsCompleted) _treeLoads[node.FullPath] = task;
        else _treeLoads.Remove(node.FullPath);
        return task;
    }

    private async Task LoadChildrenCoreAsync(DirNode node, IReadOnlyList<PosixEntry>? entries)
    {
        try
        {
            var dirs = entries != null
                ? entries.Where(x => x.IsDir).ToList()
                : await GetTreeListingAsync(node.FullPath);

            node.Children.Clear();

            foreach (var d in dirs.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var child = GetNode(d.FullPath, d.Name);
                child.HasChildren = _hasKids.Contains(d.FullPath);
                node.Children.Add(child);
            }
            node.HasChildren = node.Children.Count > 0;
            node.IsLoaded = true;
        }
        catch { }
        finally
        {
            _treeLoads.Remove(node.FullPath);
        }
    }

    /// <summary>
    /// 后台补"这个目录下面还有没有子目录"（只为了展开箭头）。
    /// 只在需要时发一次 find：祖先链、导航路径上的中间节点都不补；
    ///   同一节点一次刷新周期内只补一次；结果直接就地改行，不整树重建。
    /// </summary>
    private void QueueEnrich(DirNode node)
    {
        if (_fs == null) return;
        if (!node.IsExpanded || node.Children.Count == 0 || node.Children.Count > 200) return;

        var key = node.FullPath.TrimEnd('/');
        if (key.Length == 0) key = "/";
        if (!_enriched.Add(key)) return;

        EnrichChildrenAsync(node, key).Safe("补全目录箭头");
    }

    private async Task EnrichChildrenAsync(DirNode node, string key)
    {
        if (_fs == null) return;

        _enrichCts?.Cancel();
        var cts = new CancellationTokenSource();
        _enrichCts = cts;
        try
        {
            // 稍等一下再发命令：用户只是"路过"某个目录时，不该让它抢在当前目录的 ls 前面
            await Task.Delay(280, cts.Token);

            // 不用 `find -type d`：/sdcard 是 FUSE，find 会静默返回空（踩坑清单第 9 条）。
            //   改成 shell glob：对每个子目录试 `"$d"/*/`，匹配到就说明它还有下一级。
            //   一条命令、不 fork，FUSE 和 ext4 都可靠。
            var paths = string.Join(" ", node.Children.Select(c => Sh.Q(c.FullPath)));
            var deep = await _fs.RunCommandAsync(
                "for d in " + paths + "; do set -- \"$d\"/*/; [ -e \"$1\" ] && echo \"$d\"; done",
                true, cts.Token);

            var parents = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in deep.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("[")) continue;   // RunCommandAsync 末尾的 [退出码] 行
                parents.Add(t);
            }

            var changed = false;
            foreach (var kv in parents) _hasKids.Add(kv);

            foreach (var child in node.Children)
            {
                var has = parents.Contains(child.FullPath);
                if (child.HasChildren == has) continue;
                child.HasChildren = has;
                changed = true;
            }

            if (changed) ScheduleRebuild();
        }
        catch (OperationCanceledException)
        {
            // 被新的导航打断：允许下次再试
            _enriched.Remove(key);
        }
        catch { }
    }

    /// <summary>点箭头：展开/收起（展开时懒加载子目录）</summary>
    public async Task ToggleNodeAsync(DirNode? node)
    {
        if (node == null) return;
        if (node.IsExpanded)
        {
            node.IsExpanded = false;
            // 收起时把子孙的展开状态一并清掉。
            //   以前只改自己：大项收起了，但下面那些曾经展开过的小项状态还留着，
            //   再展开时"一开全开"，看起来就像"小项没被正常关闭"。
            CollapseDescendants(node);
        }
        else
        {
            if (!node.IsLoaded) await LoadChildrenAsync(node);
            node.IsExpanded = true;
            QueueEnrich(node);
        }
        RebuildRows();
    }

    private static void CollapseDescendants(DirNode node)
    {
        foreach (var child in node.Children)
        {
            if (!child.IsExpanded) continue;
            child.IsExpanded = false;
            CollapseDescendants(child);
        }
    }

    /// <summary>置顶行没有 Node 时（还没建立节点），按路径兜底切换</summary>
    public async Task ToggleNodeByPathAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (!_nodes.TryGetValue(path, out var node)) return;
        await ToggleNodeAsync(node);
    }

    /// <summary>刷新整棵树（保留各分支展开状态）</summary>
    public async Task RefreshTreeAsync()
    {
        // 刷新 = 用户明确要求看到最新内容：树缓存、箭头缓存全部作废
        _treeCache.Clear();
        _hasKids.Clear();
        _enriched.Clear();
        _listCache.Remove(CurrentPath);

        foreach (var n in _nodes.Values) { n.IsLoaded = false; n.Children.Clear(); }
        if (_rootNode != null) { _rootNode.IsExpanded = true; }

        RebuildRows();
        await SyncTreeAsync(CurrentPath);
    }

    /// <summary>拍平成可见行（pinned 置顶 + 各展开分支），只做增量 Patch，不整体 Reset</summary>
    private void RebuildRows()
    {
        var rows = new List<TreeRow>(TreeRows.Count + 8);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        _rowKeysUsed.Clear();

        // 置顶项与真实节点合并：
        //   /sdcard 既是符号链接、又挂在 / 下面，同一个节点会被两条路径走到，
        //   以前就会在树里出现两行指向同一个目录 —— 点第二行"没反应"（其实已经在那个目录了）。
        //   现在：置顶项优先占位，并接管该节点的展开状态与子节点，真实节点遇到已见过的路径直接跳过。
        var pinned = new[]
        {
            ("内部存储", DevicePaths.Sdcard),
            ("Root 目录", DevicePaths.AdbData),
            ("根目录",   "/"),
        };

        foreach (var (name, path) in pinned)
        {
            // 钉住行必须拿到真实节点：以前只 TryGetValue，节点还没建立时 node=null，
            //   于是箭头按 HasChildren=false 隐藏、点箭头又走 ToggleNodeByPathAsync 找不到节点，
            //   表现就是"钉住的 Root 目录展不开、也没有箭头"。
            var node = GetNode(path, name);
            if (!node.HasChildren) node.HasChildren = true;   // 这三处必然是目录，先给箭头，展开后再按实际情况更新

            seen.Add(NormalizePath(path));
            rows.Add(Row(name, path, 0, true,
                node.IsExpanded, node.HasChildren,
                SamePath(path, CurrentPath), node));

            if (node.IsExpanded)
                foreach (var c in node.Children) EmitNode(c, rows, seen, 1);
        }

        // 根目录已经作为置顶行渲染；只有它处于展开状态时才输出子节点。
        // 以前这里无条件 Emit，导致点“根目录”的收起箭头后子节点仍然在列表里（收不起来）。
        if (_rootNode is { IsExpanded: true })
            foreach (var c in _rootNode.Children) EmitNode(c, rows, seen, 1);

        // 行对象按路径缓存复用；已经不可见的路径顺手从缓存里摘掉，避免越用越大
        if (_rowCache.Count > 0)
        {
            foreach (var key in _rowCache.Keys.ToList())
                if (!_rowKeysUsed.Contains(key)) _rowCache.Remove(key);
        }

        var swPatch = System.Diagnostics.Stopwatch.StartNew();
        TreeRows.Patch(rows);
        NotifyTreeVisual();      // 让视图重算"当前项高亮"的位置（不是重绑 ItemsSource）
        if (PerfOn) Perf($"rows new={rows.Count} patch={swPatch.ElapsedMilliseconds}ms");
    }

    /// <summary>递归输出可见节点；同一条路径只出一次（含其子树）</summary>
    private void EmitNode(DirNode node, List<TreeRow> rows, HashSet<string> seen, int depth)
    {
        var key = NormalizePath(node.FullPath);
        if (!seen.Add(key)) return;

        rows.Add(Row(node.Name, node.FullPath, depth, false,
            node.IsExpanded, node.HasChildren, SamePath(node.FullPath, CurrentPath), node));

        if (!node.IsExpanded) return;
        foreach (var c in node.Children) EmitNode(c, rows, seen, depth + 1);
    }

    private static string NormalizePath(string path)
    {
        var p = path.TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }

    /// <summary>后台补箭头完成后合并多次重建请求，一个 UI 帧最多重建一次</summary>
    private void ScheduleRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _rebuildQueued = false;
            RebuildRows();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private readonly Dictionary<string, TreeRow> _rowCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rowKeysUsed = new(StringComparer.Ordinal);
    /// <summary>
    /// 点树上的目录时先把高亮移过去（ls 还要 100~200ms，先给反馈），
    /// 真正的路径切换完成后 RebuildRows 会再校准一次。
    /// </summary>
    public void PreviewTreeCurrent(string path)
    {
        var changed = false;
        foreach (var row in TreeRows)
        {
            var want = SamePath(row.FullPath, path);
            if (row.IsCurrent == want) continue;
            row.IsCurrent = want;
            changed = true;
        }
        if (changed) NotifyTreeVisual();
    }

    private bool _rebuildQueued;

    /// <summary>
    /// 取这一行对应的（可复用）对象：同一个路径永远是同一个 TreeRow 实例，
    /// 内容变了就地改值 —— 只有真正新增/删除的行才会让 ItemsControl 建/拆容器。
    /// </summary>
    private TreeRow Row(string name, string path, int depth, bool pinned,
        bool expanded, bool hasChildren, bool current, DirNode? node)
    {
        var key = (pinned ? "P:" : "N:") + path;
        _rowKeysUsed.Add(key);

        if (!_rowCache.TryGetValue(key, out var row))
        {
            row = new TreeRow();
            _rowCache[key] = row;
        }

        row.Name = name;
        row.FullPath = path;
        row.Depth = depth;
        row.IsPinned = pinned;
        row.IsExpanded = expanded;
        row.HasChildren = hasChildren;
        row.IsCurrent = current;
        row.Node = node;
        return row;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);

    // ---------------- 文件操作 ----------------

    public Task UploadAsync(IEnumerable<string> localFiles) => UploadToAsync(localFiles, CurrentPath);

    /// <summary>批量下载多个文件/文件夹到同一个本地目录</summary>
    public async Task BatchDownloadAsync(IEnumerable<PosixEntry> items, string localDir)
    {
        var fs=_fs;if(fs==null)return;
        var list = items.ToList();
        if (list.Count == 0) return;

        Directory.CreateDirectory(localDir);
        var ok = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if(!ReferenceEquals(fs,_fs)){LogLine("BATCH DL 设备已切换，停止后续下载");break;}
            var it = list[i];
            StatusText = $"正在下载 {i + 1}/{list.Count}：{it.DisplayName}";
            try
            {
                if (it.IsDir)
                {
                    var local = await PrepareDragFileAsync(it);
                    if (!string.IsNullOrEmpty(local))
                    {
                        try{File.Copy(local!, Path.Combine(localDir, Path.GetFileName(local!)), true);ok++;}
                        finally{try{File.Delete(local!);Directory.Delete(Path.GetDirectoryName(local!)!);}catch{}}
                    }
                }
                else
                {
                    await fs.DownloadAsync(it.FullPath, Path.Combine(localDir, it.Name));
                    ok++;
                }
            }
            catch(OperationCanceledException){LogLine("BATCH DL 已取消后续下载");break;}
            catch (Exception ex) { LogLine("BATCH DL " + ex.Message); }
        }
        Toast($"已下载 {ok}/{list.Count} 项到 {localDir}");
        StatusText = $"下载完成：{ok}/{list.Count}";
    }

    public async Task DownloadAsync(PosixEntry e, string localPath)
    {
        if (_fs == null) return;
        try
        {
            StatusText = "正在下载：" + e.Name;
            await _fs.DownloadAsync(e.FullPath, localPath);
            StatusText = "已保存到：" + localPath;
        }
        catch (Exception ex)
        {
            StatusText = "下载失败：" + ex.Message;
            LogLine("DOWNLOAD " + e.FullPath + " => " + ex.Message);
        }
    }

    public async Task DeleteAsync(IEnumerable<PosixEntry> items)
    {
        if (_fs == null) return;
        var list = items.ToList();
        if (list.Count == 0) return;
        InvalidatePrefetch();
        try
        {
            await _fs.DeleteAsync(list.Select(i => i.FullPath));
            await NavigateAsync(CurrentPath);
            StatusText = $"已删除 {list.Count} 个项目";
        }
        catch (Exception ex)
        {
            StatusText = "删除失败：" + ex.Message;
            LogLine("DELETE => " + ex.Message);
        }
    }

    public async Task RenameAsync(PosixEntry e, string newName)
    {
        if (_fs == null || string.IsNullOrWhiteSpace(newName) || newName == e.Name) return;
        if (!IsValidName(newName)) { StatusText = "名称不能包含 / ，也不能是 . 或 .."; return; }
        InvalidatePrefetch();
        try
        {
            await _fs.RenameAsync(e.FullPath, newName);
            await NavigateAsync(CurrentPath);
            StatusText = "已重命名为：" + newName;
        }
        catch (Exception ex)
        {
            StatusText = "重命名失败：" + ex.Message;
            LogLine("RENAME => " + ex.Message);
        }
    }

    public async Task NewFolderAsync(string name)
    {
        if (_fs == null || string.IsNullOrWhiteSpace(name)) return;
        if (!IsValidName(name)) { StatusText = "名称不能包含 / ，也不能是 . 或 .."; return; }
        InvalidatePrefetch();
        try
        {
            await _fs.MkdirAsync(Sh.Join(CurrentPath, name));
            await NavigateAsync(CurrentPath);
            StatusText = "已创建文件夹：" + name;
        }
        catch (Exception ex)
        {
            StatusText = "创建文件夹失败：" + ex.Message;
            LogLine("MKDIR => " + ex.Message);
        }
    }

    public async Task NewFileAsync(string name)
    {
        if (_fs == null || string.IsNullOrWhiteSpace(name)) return;
        if (!IsValidName(name)) { StatusText = "名称不能包含 / ，也不能是 . 或 .."; return; }
        InvalidatePrefetch();
        try
        {
            await _fs.TouchAsync(Sh.Join(CurrentPath, name));
            await NavigateAsync(CurrentPath);
            StatusText = "已创建文件：" + name;
        }
        catch (Exception ex)
        {
            StatusText = "创建文件失败：" + ex.Message;
            LogLine("TOUCH => " + ex.Message);
        }
    }

    private static bool IsValidName(string name)
        => name != "." && name != ".." && !name.Contains('/') && !name.Contains('\0');

    public async Task ApplyModeAsync(PosixEntry e, string octal)
    {
        if (_fs == null) return;
        InvalidatePrefetch();
        try
        {
            await _fs.ChmodAsync(e.FullPath, octal);
            await NavigateAsync(CurrentPath);
            StatusText = $"权限已修改为 {octal}";
        }
        catch (Exception ex)
        {
            StatusText = "修改权限失败：" + ex.Message;
            LogLine("CHMOD => " + ex.Message);
        }
    }

    public async Task<string> ReadTextAsync(PosixEntry e)
        => _fs == null ? "" : await _fs.ReadTextAsync(e.FullPath);

    /// <summary>「打开方式」用：读取任意文件字节（二进制安全）</summary>
    public async Task<byte[]> ReadBytesAsync(PosixEntry e, CancellationToken ct = default)
        => _fs == null ? Array.Empty<byte>() : await _fs.ReadBytesAsync(e.FullPath, ct);

    /// <summary>「打开方式」用：下载到本机临时文件，返回路径（调用方负责删除/交给系统打开）</summary>
    public async Task<string?> DownloadToTempAsync(PosixEntry e, CancellationToken ct = default)
    {
        if (_fs == null) return null;
        var ext = Path.GetExtension(e.Name);
        var local = DevicePaths.LocalTempFile("open", string.IsNullOrWhiteSpace(ext) ? "" : ext);
        await _fs.DownloadAsync(e.FullPath, local, ct);
        return local;
    }

    public async Task WriteTextAsync(PosixEntry e, string content)
    {
        if (_fs == null) return;
        try { await _fs.WriteTextAsync(e.FullPath, content); StatusText = "已保存：" + e.Name; }
        catch (Exception ex) { StatusText = "保存失败：" + ex.Message; LogLine("WRITE => " + ex.Message); }
    }

    public async Task<string> RestoreconPreviewAsync(PosixEntry e)
        => _fs == null ? "" : await _fs.RestoreconPreviewAsync(e.FullPath);

    public async Task<string> RestoreconApplyAsync(PosixEntry e)
    {
        if (_fs == null) return "";
        var r = await _fs.RestoreconAsync(e.FullPath);
        await NavigateAsync(CurrentPath);
        return r;
    }

    public async Task<string> ChconAsync(PosixEntry e, string type)
    {
        if (_fs == null) return "";
        var r = await _fs.ChconAsync(e.FullPath, type);
        await NavigateAsync(CurrentPath);
        return r;
    }

    /// <summary>执行命令（可选择是否用 root）</summary>
    public async Task<string> RunCommandAsync(string command, bool useRoot)
    {
        if (_fs == null) return "未连接设备";
        try { return await _fs.RunCommandAsync(command, useRoot); }
        catch (Exception ex) { return "执行失败：" + ex.Message; }
    }


    public string ToolbarToggleText => ShowToolbar ? "功能 ▾" : "功能 ▸";
    public string LogToggleText => ShowLog ? "日志 ▾" : "日志 ▸";

    [RelayCommand] private void ToggleToolbar() => ShowToolbar = !ShowToolbar;
    [RelayCommand] private void ToggleLog() => ShowLog = !ShowLog;

    partial void OnShowToolbarChanged(bool value) => OnPropertyChanged(nameof(ToolbarToggleText));
    partial void OnShowLogChanged(bool value) => OnPropertyChanged(nameof(LogToggleText));


    /// <summary>把本地文件上传到指定远端目录（拖拽落点用）</summary>
    public async Task UploadToAsync(IEnumerable<string> localFiles, string targetDir)
    {
        InvalidatePrefetch();
        var fs = _fs;
        if (fs == null) { StatusText = "未连接 ADB 设备，不能上传。"; return; }
        var files = localFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) return;
        var completed = 0;
        foreach (var f in files)
        {
            try { StatusText = "正在放入：" + Path.GetFileName(f); await fs.UploadAsync(f, targetDir); completed++; }
            catch (OperationCanceledException) { StatusText = $"上传已取消，完成 {completed}/{files.Count} 项"; return; }
            catch (Exception ex) { LogLine("UPLOAD " + f + " => " + ex.Message); }
        }
        if (ReferenceEquals(fs, _fs) && targetDir == CurrentPath) await NavigateAsync(CurrentPath);
        StatusText = $"放入完成：{completed}/{files.Count} 项" + (completed < files.Count ? $"，失败 {files.Count - completed} 项（见日志）" : "") + " → " + targetDir;
    }

    /// <summary>打包远端文件夹并下载到本地</summary>
    public async Task PackAndDownloadAsync(PosixEntry dir, string remoteTar, string localPath, CancellationToken ct = default)
    {
        var fs=_fs ?? throw new InvalidOperationException("设备未连接");
        try { await fs.TarGzAsync(dir.FullPath,remoteTar,ct); await fs.DownloadAsync(remoteTar,localPath,ct); }
        finally { try{await fs.DeleteAsync(new[]{remoteTar},CancellationToken.None);}catch{} }
    }

    /// <summary>Each drag export owns its own directory; concurrent exports never share a filename.</summary>
    public async Task<string?> PrepareDragFileAsync(PosixEntry e, CancellationToken ct = default)
    {
        var fs=_fs;if(fs==null)return null;
        var tempDir=Path.Combine(Path.GetTempPath(),"DashFileDrag",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var safeName=Path.GetFileName(e.Name);
            if(safeName!=e.Name||string.IsNullOrWhiteSpace(safeName)||safeName is "." or "..")throw new IOException("文件名不适用于 Windows 导出");
            if(e.IsDir)
            {
                var remoteTar=DevicePaths.TempFile("export",".tar.gz");var local=Path.Combine(tempDir,safeName+".tar.gz");
                try{await fs.TarGzAsync(e.FullPath,remoteTar,ct);await fs.DownloadAsync(remoteTar,local,ct);return local;}
                finally{try{await fs.DeleteAsync(new[]{remoteTar},CancellationToken.None);}catch{}}
            }
            var target=Path.Combine(tempDir,safeName);await fs.DownloadAsync(e.FullPath,target,ct);return target;
        }
        catch{try{Directory.Delete(tempDir);}catch{}throw;}
    }

    // ---------------- 杂项 ----------------

    public void UpdateSelectionInfo(IEnumerable<PosixEntry> items)
    {
        var list = items.ToList();
        var total = Entries.Count;

        // 给插件 API 留一份选择快照，并通知订阅了选择变化的插件
        _pluginSelection = list
            .Select(e => e.IsDir
                ? PluginTarget.ForDirectory(e.FullPath, e.Name)
                : PluginTarget.ForFile(e.FullPath, e.Name))
            .ToList();
        _plugins?.RaiseFileSelection(_pluginSelection.FirstOrDefault());

        if (list.Count == 0)
        {
            SelectionInfo = $"{total} 个项目";
            return;
        }

        if (list.Count == 1)
        {
            var e = list[0];
            SelectionInfo = $"{e.Name}   ·   {e.Kind}   ·   {e.Perms}（{e.ModeOctal}）";
            return;
        }

        var dirs = list.Count(x => x.IsDir);
        var files = list.Count - dirs;
        var bytes = list.Where(x => !x.IsDir).Sum(x => x.Size);
        var sizeText = bytes > 0 ? "   ·   " + FormatBytes(bytes) : "";
        SelectionInfo = $"共 {total} 项  ·  已选 {list.Count} 项（{dirs} 文件夹 / {files} 文件）{sizeText}";
    }

    private static string FormatBytes(long bytes)
    {
        string[] unit = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < unit.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {unit[i]}";
    }

    // ---------------- 复制 / 粘贴 ----------------

    private readonly List<string> _clip = new();
    public bool HasClipboard => _clip.Count > 0;

    public void CopyToClipboard(IEnumerable<PosixEntry> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        _clip.Clear();
        _clip.AddRange(list.Select(x => x.FullPath));
        OnPropertyChanged(nameof(HasClipboard));
        Toast($"已复制 {list.Count} 项");
    }

    public async Task PasteAsync()
    {
        InvalidatePrefetch();
        if (_fs == null || _clip.Count == 0) return;

        var target = CurrentPath;
        var ok = 0;
        foreach (var src in _clip.ToList())
        {
            try
            {
                var name = src.TrimEnd('/').Split('/').Last();
                var dst = Sh.Join(target, name);
                if (string.Equals(src.TrimEnd('/'), dst.TrimEnd('/'), StringComparison.Ordinal)) continue;
                await _fs.RunCommandAsync($"cp -a -- {Sh.Q(src)} {Sh.Q(dst)}", true);
                ok++;
            }
            catch (Exception ex) { LogLine("PASTE " + ex.Message); }
        }

        _clip.Clear();
        OnPropertyChanged(nameof(HasClipboard));
        Toast(ok > 0 ? $"已粘贴 {ok} 项到当前目录" : "没有可粘贴的内容");
        await NavigateAsync(CurrentPath);
    }

    // 插件宿主桥接用的内部访问口（不暴露给 XAML，避免污染绑定面）
    internal RemoteFileSystem? FileSystem => _fs;
    internal IRootProvider? RootProvider => _root;
    internal IReadOnlyList<PluginTarget> PluginSelection => _pluginSelection;
    internal void AppendLog(string s) => LogLine(s);

    private static void WritePluginHostLog(string message)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Log");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "plugins.log"),
                $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private void LogLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        AppLog.Add(s);
        var line = DateTime.Now.ToString("HH:mm:ss") + "  " + s;
        void Add()
        {
            LogLines.Add(line);
            while (LogLines.Count > 300) LogLines.RemoveAt(0);
        }
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Add();
        else Avalonia.Threading.Dispatcher.UIThread.Post(Add);
    }
}
