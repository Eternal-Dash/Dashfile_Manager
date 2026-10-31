using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DashFile.PluginApi;

namespace DashFile.Services.Plugins;

/// <summary>
/// 插件加载与生命周期管理。
///
/// 设计要点：
///  - 扫描 plugins/&lt;id&gt;/plugin.json，一个插件一个目录、一个可回收 AssemblyLoadContext；
///  - 契约程序集（DashFile.PluginApi）和 Avalonia 强制共用宿主那一份，避免类型身份分裂；
///  - 单个插件初始化失败不影响其它插件，失败原因展示在插件页；
///  - 禁用 / 重载会调用 Shutdown 并卸载 ALC，用户不需要重启程序。
/// </summary>
public sealed class PluginManager
{
    private readonly string _hostVersion;
    private readonly Func<PluginManifest, IDashFilePlugin, IPluginHost> _hostFactory;
    private readonly Action<string>? _log;
    private readonly string _stateFile;
    private readonly string _dataDir;
    private readonly string _legacyStateFile;
    private readonly List<string> _roots;
    private readonly List<PluginDescriptor> _plugins = new();
    private readonly List<IPluginEventSink> _sinks = new();
    private readonly Dictionary<string, bool> _enabledState = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;

    public PluginManager(
        string hostVersion,
        IEnumerable<string> pluginRoots,
        string dataRoot,
        Func<PluginManifest, IDashFilePlugin, IPluginHost> hostFactory,
        Action<string>? log = null)
    {
        _hostVersion = hostVersion;
        _roots = pluginRoots.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _hostFactory = hostFactory;
        _log = log;

        // 插件"数据目录"必须与"扫描根"分开。以前两者都是 <dataRoot>\plugins，
        //   于是插件数据就躺在插件自己的安装目录里：卸载时勾"保留数据"是空话（删目录=删数据），
        //   覆盖升级也一定会把数据一起丢掉；plugin-state.json 还混在扫描目录里。
        _dataDir = Path.Combine(dataRoot, "plugin-data");
        Directory.CreateDirectory(_dataDir);
        _stateFile = Path.Combine(dataRoot, "plugin-state.json");
        _legacyStateFile = Path.Combine(dataRoot, "plugins", "plugin-state.json");
        LoadState();

        foreach (var root in _roots)
        {
            try
            {
                Directory.CreateDirectory(root);
                SeedReadme(root);
            }
            catch { }
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<PluginDescriptor> Plugins => _plugins;

    public IReadOnlyList<string> Roots => _roots;

    /// <summary>插件数据根目录（每个插件一个子目录，与安装目录分离）</summary>
    public string DataDirectory => _dataDir;

    public bool IsLoading => _loading;

    /// <summary>扫描目录并加载所有已启用的插件。可重复调用（会先卸载旧插件）。</summary>
    public async Task LoadAllAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            await UnloadAllAsync().ConfigureAwait(true);
            _plugins.Clear();
            _sinks.Clear();

            var manifests = Scan();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in manifests)
            {
                var descriptor = item.Descriptor;
                if (!seen.Add(descriptor.Id))
                {
                    descriptor.State = PluginLoadState.Error;
                    descriptor.Error = "插件 id 重复，已跳过：" + descriptor.Directory;
                    _plugins.Add(descriptor);
                    continue;
                }

                descriptor.Enabled = IsEnabled(descriptor);
                _plugins.Add(descriptor);

                if (!descriptor.Enabled)
                {
                    descriptor.State = PluginLoadState.Disabled;
                    continue;
                }

                if (item.Error != null)
                {
                    descriptor.State = PluginLoadState.Error;
                    descriptor.Error = item.Error;
                    continue;
                }

                await LoadOneAsync(descriptor).ConfigureAwait(true);
            }

            _log?.Invoke($"插件扫描完成：{_plugins.Count} 个，已加载 {_plugins.Count(p => p.State == PluginLoadState.Loaded)} 个");
        }
        finally
        {
            _loading = false;
            RaiseChanged();
        }
    }

    /// <summary>重载全部插件（用于开发时反复改 DLL）。</summary>
    public Task ReloadAllAsync() => LoadAllAsync();

    public async Task EnableAsync(string id)
    {
        var descriptor = Find(id);
        if (descriptor == null) return;

        descriptor.Enabled = true;
        _enabledState[id] = true;
        SaveState();

        if (descriptor.State != PluginLoadState.Loaded)
        {
            descriptor.State = PluginLoadState.Disabled;
            descriptor.Error = null;
            await LoadOneAsync(descriptor).ConfigureAwait(true);
        }
        RaiseChanged();
    }

    public async Task DisableAsync(string id)
    {
        var descriptor = Find(id);
        if (descriptor == null) return;

        descriptor.Enabled = false;
        _enabledState[id] = false;
        SaveState();

        await UnloadOneAsync(descriptor).ConfigureAwait(true);
        descriptor.State = PluginLoadState.Disabled;
        descriptor.Error = null;
        RaiseChanged();
    }

    public async Task ReloadOneAsync(string id)
    {
        var descriptor = Find(id);
        if (descriptor == null) return;

        await UnloadOneAsync(descriptor).ConfigureAwait(true);
        descriptor.Error = null;
        if (descriptor.Enabled) await LoadOneAsync(descriptor).ConfigureAwait(true);
        RaiseChanged();
    }

    public IReadOnlyList<PluginActionBinding> GetActions(PluginTargetKind kind)
    {
        if (kind == PluginTargetKind.None) return Array.Empty<PluginActionBinding>();
        var list = new List<PluginActionBinding>();
        foreach (var plugin in _plugins.Where(p => p.State == PluginLoadState.Loaded))
        {
            foreach (var action in plugin.Actions)
            {
                if (action.Targets.Length == 0 ||
                    action.Targets.Contains(PluginTargetKind.Any) ||
                    action.Targets.Contains(kind))
                    list.Add(new PluginActionBinding(plugin, action));
            }
        }
        return list;
    }

    public IReadOnlyList<PluginActionBinding> GetGlobalActions()
    {
        var list = new List<PluginActionBinding>();
        foreach (var plugin in _plugins.Where(p => p.State == PluginLoadState.Loaded))
            foreach (var action in plugin.GlobalActions)
                list.Add(new PluginActionBinding(plugin, action));
        return list;
    }

    public PluginDescriptor? Find(string id)
        => _plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>插件在运行期动态注册页面 / 动作后，通知宿主刷新 UI。</summary>
    public void NotifyChanged() => RaiseChanged();

    public void RaiseDeviceChanged(DeviceSnapshot snapshot)
    {
        foreach (var sink in _sinks.ToArray())
        {
            try { sink.RaiseDeviceChanged(snapshot); }
            catch (Exception ex) { _log?.Invoke("插件设备事件失败：" + ex.Message); }
        }
    }

    public void RaiseFileSelection(PluginTarget? target)
    {
        foreach (var sink in _sinks.ToArray())
        {
            try { sink.RaiseFileSelection(target); }
            catch (Exception ex) { _log?.Invoke("插件选择事件失败：" + ex.Message); }
        }
    }

    // ---------------- 内部：扫描 ----------------

    private sealed record ScanItem(PluginDescriptor Descriptor, string? Error);

    private List<ScanItem> Scan()
    {
        var result = new List<ScanItem>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in _roots)
        {
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception ex)
            {
                _log?.Invoke("插件目录枚举失败：" + root + " -> " + ex.Message);
                continue;
            }

            foreach (var dir in dirs)
            {
                var dirName = Path.GetFileName(dir);

                // 内部目录不能当插件扫：残留的 *.bak-<guid> / *.deleted-<guid> 里也有
                //   plugin.json 和 dll，会被当成"同名 id 的重复插件"，甚至让卸载过的插件复活。
                if (IsReservedDirectory(dirName)) continue;

                var manifestPath = Path.Combine(dir, PluginApiInfo.ManifestFileName);
                if (!File.Exists(manifestPath)) continue;
                if (!known.Add(dirName)) continue; // 同名目录只取第一个根

                PluginManifest? manifest = null;
                string? error = null;
                try
                {
                    var json = File.ReadAllText(manifestPath);
                    manifest = JsonSerializer.Deserialize<PluginManifest>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    });
                }
                catch (Exception ex)
                {
                    error = "plugin.json 解析失败：" + ex.Message;
                }

                manifest ??= new PluginManifest
                {
                    Id = Path.GetFileName(dir),
                    Name = Path.GetFileName(dir),
                };

                if (string.IsNullOrWhiteSpace(manifest.Id))
                {
                    manifest.Id = dirName;
                    error ??= "plugin.json 缺少 id";
                }
                else if (!PluginPackage.IsSafeId(manifest.Id))
                {
                    // 之前只有 .dfp 安装路径校验 id；这里手工放进来的目录/改过的 plugin.json
                    //   一旦带 ..\ 之类，后续 Path.Combine(root, id) 会变成越界删除，必须挡住。
                    error ??= "plugin.json 的 id 不合法（只能是字母/数字/._-）";
                    manifest.Id = dirName;
                }

                if (manifest.ApiVersion > PluginApiInfo.Version)
                    error ??= $"插件需要 API v{manifest.ApiVersion}，当前宿主只支持 v{PluginApiInfo.Version}";
                else if (!string.IsNullOrWhiteSpace(manifest.MinHostVersion) &&
                         !VersionAtLeast(_hostVersion, manifest.MinHostVersion!))
                    error ??= $"插件要求 DashFile ≥ {manifest.MinHostVersion}，当前 {_hostVersion}";

                var assemblyPath = ResolveAssemblyPath(dir, manifest, ref error);
                var descriptor = new PluginDescriptor(manifest, dir, assemblyPath);
                result.Add(new ScanItem(descriptor, error));
            }
        }

        return result;
    }

    private static string ResolveAssemblyPath(string dir, PluginManifest manifest, ref string? error)
    {
        var assembly = manifest.Assembly;
        if (string.IsNullOrWhiteSpace(assembly))
        {
            // 允许省略：找目录里唯一一个与目录同名的 dll，或唯一一个 dll
            try
            {
                var dlls = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly);
                var same = dlls.FirstOrDefault(d =>
                    string.Equals(Path.GetFileNameWithoutExtension(d), Path.GetFileName(dir),
                        StringComparison.OrdinalIgnoreCase));
                assembly = Path.GetFileName(same ?? (dlls.Length == 1 ? dlls[0] : ""));
            }
            catch { }
        }

        if (string.IsNullOrWhiteSpace(assembly))
        {
            error ??= "plugin.json 缺少 assembly，且目录里找不到唯一的 dll";
            return "";
        }

        var full = Path.GetFullPath(Path.Combine(dir, assembly));
        var root = Path.GetFullPath(dir);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            error ??= "assembly 路径越出了插件目录";
            return "";
        }

        if (!File.Exists(full))
        {
            error ??= "找不到入口程序集：" + assembly;
            return "";
        }
        return full;
    }


    // ---------------- 安装 / 卸载 .dfp ----------------

    public string PrimaryRoot => _roots[0];

    // 扫描时要跳过的内部目录：安装临时区 / 覆盖安装的旧版本备份 / 删除失败的待清理目录
    private const string ReservedPrefixInstall = ".install-";
    private const string ReservedPrefixBackup = ".bak-";
    private const string ReservedPrefixDeleted = ".deleted-";

    private static bool IsReservedDirectory(string name)
        => name.StartsWith(ReservedPrefixInstall, StringComparison.OrdinalIgnoreCase)
           || name.Contains(ReservedPrefixBackup, StringComparison.OrdinalIgnoreCase)
           || name.Contains(ReservedPrefixDeleted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 安装 .dfp（zip）插件包。只接受 .dfp；已有同 id 插件时必须显式 replaceExisting。
    /// 返回安装后的描述符（加载失败时 State=Error，但仍返回，方便 UI 展示原因）。
    /// </summary>
    public async Task<PluginDescriptor?> InstallPackageAsync(string packagePath, bool replaceExisting,
        CancellationToken cancellationToken = default)
    {
        if (!PluginPackage.IsPackage(packagePath))
            throw new InvalidOperationException("只接受 .dfp 插件包");
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("找不到插件包", packagePath);

        var root = PrimaryRoot;
        Directory.CreateDirectory(root);
        // 解压目录必须和目标在同一个卷上：Directory.Move 不支持跨卷，
        //   以前解压到 %TEMP%（通常是 C:），程序装在 D: 时安装 100% 失败。
        var temp = Path.Combine(root, ReservedPrefixInstall + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string? backup = null;
        string? target = null;

        try
        {
            var packageRoot = PluginPackage.Extract(packagePath, temp);
            var manifest = PluginPackage.ReadManifest(packageRoot);
            target = Path.Combine(root, manifest.Id);

            var existing = Find(manifest.Id);
            if (Directory.Exists(target) || existing != null)
            {
                if (!replaceExisting)
                    throw new InvalidOperationException($"插件 {manifest.Id} 已安装；如需覆盖请选择替换。");
                if (existing != null) await UnloadOneAsync(existing).ConfigureAwait(true);
                if (Directory.Exists(target))
                {
                    backup = target + ".bak-" + Guid.NewGuid().ToString("N");
                    Directory.Move(target, backup);
                }
            }

            // 确保没有残留目录（大小写不敏感文件系统上 id 可能只差大小写）
            if (Directory.Exists(target) && !PathsEqual(target, packageRoot))
                Directory.Delete(target, recursive: true);

            if (!PathsEqual(target, packageRoot))
                Directory.Move(packageRoot, target);

            await LoadAllAsync().ConfigureAwait(true);
            var installed = Find(manifest.Id);
            _log?.Invoke($"插件安装：{manifest.Id} <- {packagePath}");
            if (backup != null)
            {
                try { Directory.Delete(backup, recursive: true); } catch { }
                backup = null;
            }
            return installed;
        }
        catch
        {
            // 安装失败尽量恢复旧版本
            try
            {
                if (target != null && Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
                // CA1508 会把这里判成"backup 恒为 null"：它看不到 await LoadAllAsync() 也会抛，
                // 而那条路径上旧版本已经改名到 backup、新版本已经就位 —— 这里必须回滚。
#pragma warning disable CA1508
                if (backup != null && Directory.Exists(backup) && target != null)
                    Directory.Move(backup, target);
#pragma warning restore CA1508
            }
            catch { }
            throw;
        }
        finally
        {
            if (backup != null)
            {
                try { if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true); } catch { }
            }
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    /// <summary>卸载插件：先停用/卸载 ALC，再删目录；可选删除插件数据（设置/缓存）。</summary>
    public async Task UninstallAsync(string id, bool deleteData, CancellationToken cancellationToken = default)
    {
        var descriptor = Find(id);
        if (descriptor == null)
        {
            // 没有运行期描述符时也允许清理残留目录（例如插件加载失败后）
            foreach (var root in _roots)
            {
                var dir = Path.Combine(root, id);
                if (Directory.Exists(dir)) DeleteDirectoryWithRetry(dir);
            }
            _enabledState.Remove(id);
            SaveState();
            if (deleteData)
            {
                var data = Path.Combine(_dataDir, id);
                try { if (Directory.Exists(data)) DeleteDirectoryWithRetry(data); } catch { }
            }
            RaiseChanged();
            return;
        }

        await UnloadOneAsync(descriptor).ConfigureAwait(true);
        await Task.Yield();

        var failures = new List<string>();
        foreach (var root in _roots)
        {
            var dir = Path.Combine(root, descriptor.Id);
            if (!Directory.Exists(dir)) continue;
            try
            {
                DeleteDirectoryWithRetry(dir);
            }
            catch (Exception ex)
            {
                failures.Add($"{dir}: {ex.Message}");
                // 文件仍被占用时先改名，避免下次扫描又加载旧版本
                try
                {
                    var moved = dir + ".deleted-" + Guid.NewGuid().ToString("N");
                    Directory.Move(dir, moved);
                    failures.RemoveAt(failures.Count - 1);
                    _log?.Invoke("插件目录暂被占用，已改名待清理：" + moved);
                }
                catch { }
            }
        }

        _plugins.RemoveAll(p => string.Equals(p.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
        _enabledState.Remove(descriptor.Id);
        SaveState();

        if (deleteData)
        {
            var data = Path.Combine(_dataDir, id);
            try { if (Directory.Exists(data)) DeleteDirectoryWithRetry(data); }
            catch (Exception ex) { _log?.Invoke("删除插件数据失败：" + ex.Message); }
        }

        _log?.Invoke($"插件卸载：{descriptor.Name}");
        RaiseChanged();

        if (failures.Count > 0)
            throw new IOException("插件已停用，但部分文件删除失败：\n" + string.Join("\n", failures));
    }

    private static void DeleteDirectoryWithRetry(string dir)
    {
        // ALC 卸载后文件句柄可能延迟释放；重试几次比直接失败好
        for (var i = 0; i < 4; i++)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (IOException) when (i < 3)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(120);
            }
            catch (UnauthorizedAccessException) when (i < 3)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(120);
            }
        }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    // ---------------- 内部：加载 / 卸载 ----------------

    private async Task LoadOneAsync(PluginDescriptor descriptor)
    {
        descriptor.Error = null;
        if (string.IsNullOrWhiteSpace(descriptor.AssemblyPath) || !File.Exists(descriptor.AssemblyPath))
        {
            descriptor.State = PluginLoadState.Error;
            descriptor.Error = "入口程序集不存在";
            return;
        }

        try
        {
            descriptor.LoadContext = new PluginLoadContext(descriptor.AssemblyPath);
            descriptor.Assembly = descriptor.LoadContext.LoadFromAssemblyPath(descriptor.AssemblyPath);

            var type = !string.IsNullOrWhiteSpace(descriptor.Manifest.EntryType)
                ? descriptor.Assembly.GetType(descriptor.Manifest.EntryType, throwOnError: false)
                : null;

            type ??= descriptor.Assembly.GetTypes().FirstOrDefault(t =>
                typeof(IDashFilePlugin).IsAssignableFrom(t) &&
                !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null);

            if (type == null)
                throw new InvalidOperationException("找不到实现 IDashFilePlugin 的公共入口类型");

            var instance = Activator.CreateInstance(type) as IDashFilePlugin
                ?? throw new InvalidOperationException("入口类型无法实例化（需要公共无参构造函数）");

            descriptor.Instance = instance;
            var host = _hostFactory(descriptor.Manifest, instance);
            descriptor.HostObject = host;
            // 插件初始化最多等 5 秒：插件挂住时不能把插件页一起拖死，超时标 Error 可重载
            // 不能 using 掉这个 CTS：超时后插件可能还在跑，立刻 Dispose 会让它后续
            //   拿 Register / 创建页面时抛 ObjectDisposedException。改成超时只 Cancel，随插件卸载回收。
            var initCts = new CancellationTokenSource();
            descriptor.InitCts = initCts;
            try
            {
                var init = instance.InitializeAsync(host, initCts.Token);
                var done = await Task.WhenAny(init, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
                if (done != init)
                {
                    initCts.Cancel();
                    _ = init.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                    throw new TimeoutException("插件初始化超过 5 秒（疑似挂起）");
                }
                await init.ConfigureAwait(true);
            }
            finally
            {
                // 初始化正常结束：CTS 留着给插件当"生命周期令牌"，卸载时一起释放
            }
            descriptor.State = PluginLoadState.Loaded;
            if (host is IPluginEventSink sink) _sinks.Add(sink);

            _log?.Invoke($"插件已加载：{descriptor.Name} v{descriptor.Manifest.Version} " +
                         $"(页面 {descriptor.Pages.Count} / 动作 {descriptor.Actions.Count})");
        }
        catch (Exception ex)
        {
            descriptor.State = PluginLoadState.Error;
            descriptor.Error = ex.GetBaseException().Message;
            _log?.Invoke($"插件加载失败：{descriptor.Name} -> {ex}");
            await UnloadOneAsync(descriptor).ConfigureAwait(true);
        }
    }

    private async Task UnloadOneAsync(PluginDescriptor descriptor)
    {
        if (descriptor.Instance != null)
        {
            try
            {
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var shutdown = descriptor.Instance.ShutdownAsync(shutdownCts.Token);
                var done = await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
                if (done != shutdown)
                {
                    _ = shutdown.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                    _log?.Invoke("插件关闭超时（5 秒）：" + descriptor.Name);
                }
                else await shutdown.ConfigureAwait(true);
            }
            catch (Exception ex) { _log?.Invoke("插件关闭失败：" + ex.Message); }
        }

        // 插件可能改过 UI 资源；禁用/重载时由宿主统一恢复，避免主题颜色残留
        if (descriptor.HostObject is IDisposable disposableHost)
        {
            try { disposableHost.Dispose(); }
            catch (Exception ex) { _log?.Invoke("插件 UI 清理失败：" + ex.Message); }
        }

        _sinks.RemoveAll(s => ReferenceEquals(s, descriptor.HostObject));

        descriptor.Instance = null;
        descriptor.HostObject = null;
        descriptor.Assembly = null;
        try { descriptor.InitCts?.Cancel(); } catch { }
        try { descriptor.InitCts?.Dispose(); } catch { }
        descriptor.InitCts = null;
        descriptor.ClearRegistrations();

        var context = descriptor.LoadContext;
        descriptor.LoadContext = null;
        if (context != null)
        {
            try { context.Unload(); } catch { }
        }
    }

    private async Task UnloadAllAsync()
    {
        foreach (var plugin in _plugins.ToArray())
            await UnloadOneAsync(plugin).ConfigureAwait(true);
        _sinks.Clear();
    }

    // ---------------- 状态持久化 ----------------

    private void LoadState()
    {
        try
        {
            // 老版本把状态文件写在扫描目录里，搬到新位置后要能接着读
            var source = File.Exists(_stateFile) ? _stateFile
                : File.Exists(_legacyStateFile) ? _legacyStateFile
                : null;
            if (source == null) return;
            var json = File.ReadAllText(source);
            var map = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
            if (map == null) return;
            foreach (var kv in map) _enabledState[kv.Key] = kv.Value;
        }
        catch { }
    }

    private readonly object _stateGate = new();

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            // 序列化正在被另一个线程改的 Dictionary 会抛"集合已修改"，
            //   以前被 catch{} 吞掉 = 启用状态静默丢失。这里先取快照再写。
            string json;
            lock (_stateGate)
            {
                var snapshot = new Dictionary<string, bool>(_enabledState, StringComparer.OrdinalIgnoreCase);
                json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            }
            AtomicFile.WriteAllText(_stateFile, json);
        }
        catch (Exception ex)
        {
            _log?.Invoke("插件状态保存失败：" + ex.Message);
        }
    }

    private bool IsEnabled(PluginDescriptor descriptor)
    {
        if (_enabledState.TryGetValue(descriptor.Id, out var enabled)) return enabled;
        return descriptor.Manifest.Enabled;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static bool VersionAtLeast(string host, string required)
    {
        static Version? Parse(string s)
        {
            var cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) s = s[..cut];
            return Version.TryParse(s, out var v) ? v : null;
        }
        var a = Parse(host);
        var b = Parse(required);
        if (a == null || b == null) return true;
        return a >= b;
    }

    private static void SeedReadme(string root)
    {
        var readme = Path.Combine(root, "README.txt");
        if (File.Exists(readme)) return;
        File.WriteAllText(readme,
            "DashFile 插件目录\r\n" +
            "=================\r\n\r\n" +
            "安装：把 .dfp 插件包拖进「插件」页，或点「安装 .dfp」。\r\n" +
            "本目录下的每个子目录是一个已安装插件：\r\n" +
            "  plugins\\<插件id>\\plugin.json\r\n" +
            "  plugins\\<插件id>\\<入口程序集>.dll\r\n\r\n" +
            "plugin.json 示例：\r\n" +
            "{\r\n" +
            "  \"id\": \"com.example.hello\",\r\n" +
            "  \"name\": \"Hello Plugin\",\r\n" +
            "  \"version\": \"1.0.0\",\r\n" +
            "  \"author\": \"you\",\r\n" +
            "  \"description\": \"示例插件\",\r\n" +
            "  \"assembly\": \"HelloPlugin.dll\",\r\n" +
            "  \"entryType\": \"HelloPlugin.HelloPlugin\",\r\n" +
            "  \"apiVersion\": 1\r\n" +
            "}\r\n\r\n" +
            "插件开发说明见程序目录 docs\\插件开发指南.md。\r\n");
    }
}
