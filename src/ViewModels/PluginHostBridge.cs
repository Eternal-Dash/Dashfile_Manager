using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DashFile.Models;
using DashFile.PluginApi;
using DashFile.Services;
using DashFile.Services.Plugins;
using DashFile.Views;

namespace DashFile.ViewModels;

/// <summary>
/// 把一个插件和 DashFile 的实际能力桥接起来。
/// 插件只看到 DashFile.PluginApi 里的接口，这里负责把调用转成 VM / Services 的操作。
/// </summary>
internal sealed class PluginHostBridge : IPluginHost, IPluginEventSink, IDisposable
{
    private readonly MainViewModel _vm;
    private readonly PluginManager _manager;
    private readonly PluginManifest _manifest;

    private readonly PluginEvents _events = new();

    public PluginHostBridge(MainViewModel vm, PluginManager manager, PluginManifest manifest,
        IDashFilePlugin instance)
    {
        _vm = vm;
        _manager = manager;
        _manifest = manifest;

        var safeId = SafeFileName(string.IsNullOrWhiteSpace(manifest.Id) ? "plugin" : manifest.Id);
        // 插件数据放独立目录，不放在插件自己的安装目录里：
        //   否则"卸载时保留数据"没意义（删插件=删数据），覆盖升级也会丢数据。
        DataDirectory = Path.Combine(AppPaths.PluginData, safeId);
        Directory.CreateDirectory(DataDirectory);

        var own = instance.Metadata;
        Metadata = new PluginMetadata
        {
            Id = string.IsNullOrWhiteSpace(own?.Id) ? manifest.Id : own!.Id,
            Name = string.IsNullOrWhiteSpace(own?.Name) ? manifest.Name : own!.Name,
            Version = string.IsNullOrWhiteSpace(own?.Version) ? manifest.Version : own!.Version,
            Author = string.IsNullOrWhiteSpace(own?.Author) ? manifest.Author : own!.Author,
            Description = string.IsNullOrWhiteSpace(own?.Description) ? manifest.Description : own!.Description,
            Homepage = own?.Homepage ?? manifest.Homepage,
        };

        Ui = new PluginUiApi(this);
        Device = new PluginDeviceApi(this);
        Files = new PluginFilesApi(this);
        Apps = new PluginAppsApi(this);
        Modules = new PluginModulesApi(this);
        Settings = new PluginSettingsStore(Path.Combine(DataDirectory, "settings.json"));
        Log = new PluginLogApi(this);
    }

    public PluginMetadata Metadata { get; }

    public string HostVersion => AppInfo.Version;

    public string DataDirectory { get; }

    public IPluginUi Ui { get; }

    public IPluginDevice Device { get; }

    public IPluginFiles Files { get; }

    public IPluginApps Apps { get; }

    public IPluginModules Modules { get; }

    public IPluginSettings Settings { get; }

    public IPluginLog Log { get; }

    public IPluginEvents Events => _events;

    internal MainViewModel ViewModel => _vm;

    /// <summary>插件被禁用 / 重载时，清掉它做的所有 UI 资源覆盖。</summary>
    public void Dispose()
    {
        if (Ui is PluginUiApi ui) ui.ClearResourceOverrides();
    }

    internal string PluginId => _manifest.Id;

    /// <summary>插件推送"顶部信息"：宿主把它显示在插件名右侧（蓝色区域），不再让插件自己画标题卡</summary>
    public void SetHeader(PluginHeaderInfo? header)
        => _vm.ApplyPluginHeader(_manifest.Id,
            header?.Subtitle, header?.Badges, header?.Status);

    public void SetStatus(string? status)
        => _vm.ApplyPluginHeader(_manifest.Id, null, null, status, onlyStatus: true);

    public void AddPage(PluginPage page)
    {
        if (page == null) return;
        RunOnUi(() =>
        {
            _manager.Find(_manifest.Id)?.AddPage(page);
            _manager.NotifyChanged();
        });
    }

    public void AddAction(PluginAction action)
    {
        if (action == null) return;
        RunOnUi(() =>
        {
            _manager.Find(_manifest.Id)?.AddAction(action);
            _manager.NotifyChanged();
        });
    }

    public void AddGlobalAction(PluginAction action)
    {
        if (action == null) return;
        RunOnUi(() =>
        {
            _manager.Find(_manifest.Id)?.AddGlobalAction(action);
            _manager.NotifyChanged();
        });
    }

    public void RaiseDeviceChanged(DeviceSnapshot snapshot)
        => RunOnUi(() => _events.RaiseDeviceChanged(snapshot));

    public void RaiseFileSelection(PluginTarget? target)
        => RunOnUi(() => _events.RaiseFileSelection(target));

    // ---------------- UI 线程辅助 ----------------

    internal static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    internal static async Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        if (Dispatcher.UIThread.CheckAccess()) return await work().ConfigureAwait(true);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { tcs.SetResult(await work().ConfigureAwait(true)); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return await tcs.Task.ConfigureAwait(true);
    }

    internal static async Task OnUiAsync(Func<Task> work)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            await work().ConfigureAwait(true);
            return;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { await work().ConfigureAwait(true); tcs.SetResult(true); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        await tcs.Task.ConfigureAwait(true);
    }

    internal static Window? OwnerWindow
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static string SafeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "plugin" : value;
    }

    // ================= UI =================

    private sealed class PluginUiApi : IPluginUi
    {
        private readonly PluginHostBridge _owner;
        public PluginUiApi(PluginHostBridge owner) => _owner = owner;

        public void Toast(string message) => RunOnUi(() => _owner._vm.Toast(message ?? ""));

        public async Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            await OnUiAsync(async () =>
            {
                var owner = OwnerWindow;
                if (owner == null) { _owner._vm.Toast(message ?? ""); return; }
                await ConfirmDialog.ShowAsync(owner, title ?? "提示", message ?? "");
            }).ConfigureAwait(true);
        }

        public async Task<bool> ConfirmAsync(string title, string message, string okText = "确定",
            string cancelText = "取消", CancellationToken cancellationToken = default)
        {
            return await OnUiAsync(async () =>
            {
                var owner = OwnerWindow;
                if (owner == null) return false;
                return await ConfirmDialog.ShowAsync(owner, title ?? "确认", message ?? "");
            }).ConfigureAwait(true);
        }

        public async Task<string?> PromptAsync(string title, string label, string initialValue = "",
            CancellationToken cancellationToken = default)
        {
            return await OnUiAsync(async () =>
            {
                var owner = OwnerWindow;
                if (owner == null) return null;
                return await InputDialog.ShowAsync(owner, title ?? "输入", label ?? "", initialValue ?? "");
            }).ConfigureAwait(true);
        }

        public async Task<string?> OpenFileAsync(string title, params string[] patterns)
        {
            var list = await OpenFilesAsync(title, patterns).ConfigureAwait(true);
            return list.FirstOrDefault();
        }

        public async Task<IReadOnlyList<string>> OpenFilesAsync(string title, params string[] patterns)
        {
            return await OnUiAsync(async () =>
            {
                var owner = OwnerWindow;
                if (owner == null) return (IReadOnlyList<string>)Array.Empty<string>();

                var options = new FilePickerOpenOptions
                {
                    Title = title ?? "选择文件",
                    AllowMultiple = true,
                    FileTypeFilter = BuildFileTypes(patterns),
                };
                var files = await owner.StorageProvider.OpenFilePickerAsync(options);
                return files.Select(f => f.TryGetLocalPath())
                            .Where(p => !string.IsNullOrWhiteSpace(p))
                            .Cast<string>()
                            .ToList();
            }).ConfigureAwait(true);
        }

        public async Task<string?> SaveFileAsync(string title, string suggestedFileName, params string[] patterns)
        {
            return await OnUiAsync(async () =>
            {
                var owner = OwnerWindow;
                if (owner == null) return null;

                // "*.*" 取出来是 "*"，DefaultExtension 设成 "*" 会让保存对话框加出奇怪的后缀
                var ext = patterns?.FirstOrDefault(p => p.StartsWith("*.", StringComparison.Ordinal))?[2..] ?? "";
                if (ext is "*" or "") ext = "";
                var options = new FilePickerSaveOptions
                {
                    Title = title ?? "保存文件",
                    SuggestedFileName = suggestedFileName ?? "output.bin",
                    DefaultExtension = ext,
                    ShowOverwritePrompt = true,
                    FileTypeChoices = BuildFileTypes(patterns),
                };
                var file = await owner.StorageProvider.SaveFilePickerAsync(options);
                return file?.TryGetLocalPath();
            }).ConfigureAwait(true);
        }

        public async Task<string?> PickFolderAsync(string title)
        {
            return await OnUiAsync(async () =>
            {
                var owner = OwnerWindow;
                if (owner == null) return null;
                var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = title ?? "选择文件夹",
                    AllowMultiple = false,
                });
                return folders.FirstOrDefault()?.TryGetLocalPath();
            }).ConfigureAwait(true);
        }

        public IPluginProgress CreateProgress(string title) => new PluginProgress(_owner, title ?? "处理中");

        public void RunOnUiThread(Action action)
        {
            if (action != null) RunOnUi(action);
        }

        public Task RunOnUiThreadAsync(Action action)
            => action == null ? Task.CompletedTask : OnUiAsync(() => { action(); return Task.CompletedTask; });

        // ---- UI 资源覆盖：插件可以改主题色、圆角、行高等 ----

        private readonly List<ResourceOverrideSession> _resourceOverrides = new();

        public IDisposable OverrideResource(string key, object value)
        {
            if (string.IsNullOrWhiteSpace(key)) return EmptyDisposable.Instance;
            var session = new ResourceOverrideSession(_owner, key, value);
            lock (_resourceOverrides) _resourceOverrides.Add(session);
            RunOnUi(session.Apply);
            return session;
        }

        public IDisposable OverrideResourceColor(string key, Color color)
            => OverrideResource(key, color);

        public void ClearResourceOverrides()
        {
            ResourceOverrideSession[] copy;
            lock (_resourceOverrides)
            {
                copy = _resourceOverrides.ToArray();
                _resourceOverrides.Clear();
            }

            for (var i = copy.Length - 1; i >= 0; i--) copy[i].Restore();
        }

        internal void Forget(ResourceOverrideSession session)
        {
            lock (_resourceOverrides) _resourceOverrides.Remove(session);
        }

        private static IReadOnlyList<FilePickerFileType>? BuildFileTypes(string[]? patterns)
        {
            if (patterns == null || patterns.Length == 0) return null;
            var list = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            if (list.Length == 0) return null;
            return new[] { new FilePickerFileType("支持的文件") { Patterns = list } };
        }
    }

    /// <summary>一条 UI 资源覆盖。Dispose 后恢复宿主原值。</summary>
    private sealed class ResourceOverrideSession : IDisposable
    {
        private readonly PluginUiApi _ui;
        private readonly string _key;
        private readonly object _value;
        private object? _original;
        private bool _hadOriginal;
        private bool _applied;
        private bool _restored;
        private readonly object _gate = new();

        public ResourceOverrideSession(PluginHostBridge owner, string key, object value)
        {
            _ui = (PluginUiApi)owner.Ui;
            _key = key;
            _value = value is Color color ? new SolidColorBrush(color) : value;
        }

        public void Apply()
        {
            lock (_gate)
            {
                if (_applied || _restored) return;
                _applied = true;
            }

            RunOnUi(() =>
            {
                var app = Application.Current;
                if (app == null) return;
                _hadOriginal = app.Resources.TryGetResource(_key, null, out var original);
                _original = original;
                app.Resources[_key] = _value;
            });
        }

        public void Restore()
        {
            lock (_gate)
            {
                if (_restored) return;
                _restored = true;
            }

            RunOnUi(() =>
            {
                var app = Application.Current;
                if (app != null)
                {
                    if (_hadOriginal) app.Resources[_key] = _original;
                    else app.Resources.Remove(_key);
                }
                _ui.Forget(this);
            });
        }

        public void Dispose() => Restore();
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }

    private sealed class PluginProgress : IPluginProgress
    {
        private readonly PluginHostBridge _owner;
        private bool _completed;

        public PluginProgress(PluginHostBridge owner, string title)
        {
            _owner = owner;
            Title = title;
        }

        public string Title { get; }

        public void Report(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            RunOnUi(() => _owner._vm.StatusText = value);
            _owner._vm.AppendLog($"[{_owner.Metadata.Name}] {value}");
        }

        public void Complete(string? message = null)
        {
            if (_completed) return;
            _completed = true;
            if (!string.IsNullOrWhiteSpace(message)) Toast(message);
        }

        public void Dispose() => _completed = true;

        private void Toast(string message) => RunOnUi(() => _owner._vm.Toast(message));
    }

    // ================= 设备 =================

    private sealed class PluginDeviceApi : IPluginDevice
    {
        private readonly PluginHostBridge _owner;
        public PluginDeviceApi(PluginHostBridge owner) => _owner = owner;

        private RemoteFileSystem? Fs => _owner._vm.FileSystem;

        public bool IsConnected => Fs != null;
        public bool IsRoot => _owner._vm.IsRoot;
        public string Serial => Fs?.Serial ?? "";
        public string RootManagerName => _owner._vm.RootImpl;
        public string Model => _owner._vm.DevModel;
        public string AndroidVersion => _owner._vm.DevAndroid;

        public async Task<PluginShellResult> ShellAsync(string command, bool root = true,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            if (fs == null) return new PluginShellResult(-1, "", "未连接设备");
            var r = await fs.ExecuteAsync(command ?? "", root, cancellationToken).ConfigureAwait(false);
            return new PluginShellResult(r.ExitCode, r.StdOut, r.StdErr);
        }

        public async Task<PluginShellResult> ExecAsync(string executable, IReadOnlyList<string> arguments,
            bool root = true, CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            if (fs == null) return new PluginShellResult(-1, "", "未连接设备");
            var r = await fs.RunArgvAsync(executable, arguments ?? Array.Empty<string>(), root, cancellationToken)
                            .ConfigureAwait(false);
            return new PluginShellResult(r.ExitCode, r.StdOut, r.StdErr);
        }

        public async Task<byte[]> ExecCaptureAsync(string command, bool root = true,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            if (fs == null) return Array.Empty<byte>();
            return await fs.ExecCaptureAsync(command ?? "", root, cancellationToken).ConfigureAwait(false);
        }

        public async Task<byte[]> ReadBytesAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            if (fs == null) throw new InvalidOperationException("未连接设备");
            return await fs.ReadBytesAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> WriteBytesAsync(string remotePath, byte[] data,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            if (fs == null) return false;
            return await fs.WriteBytesAsync(remotePath, data ?? Array.Empty<byte>(), cancellationToken)
                           .ConfigureAwait(false);
        }

        public async Task<string> ReadTextAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            if (fs == null) throw new InvalidOperationException("未连接设备");
            return await fs.ReadTextAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteTextAsync(string remotePath, string text,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            if (fs == null) throw new InvalidOperationException("未连接设备");
            await fs.WriteTextAsync(remotePath, text ?? "", cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> ExistsAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            if (fs == null) return false;
            return await fs.ExistsAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }

        public async Task DownloadAsync(string remotePath, string localPath,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            if (fs == null) throw new InvalidOperationException("未连接设备");
            await fs.DownloadAsync(remotePath, localPath, cancellationToken).ConfigureAwait(false);
        }

        public async Task UploadAsync(string localPath, string remoteDirectory,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            if (fs == null) throw new InvalidOperationException("未连接设备");
            await fs.UploadAsync(localPath, remoteDirectory, cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> PushTempFileAsync(string localPath,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            if (fs == null) throw new InvalidOperationException("未连接设备");
            return await fs.PushTempFileAsync(localPath, cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> CreateTempDirectoryAsync(CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            if (fs == null) throw new InvalidOperationException("未连接设备");
            var remote = DevicePaths.TempDirectory("plugin");
            await fs.RunCommandAsync($"mkdir -p {PluginShell.Quote(remote)} && chmod 777 {PluginShell.Quote(remote)}",
                true, cancellationToken).ConfigureAwait(false);
            return remote;
        }

        public async Task DeleteRemoteAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            if (fs == null) return;
            await fs.RunCommandAsync("rm -rf " + PluginShell.Quote(remotePath ?? ""), true, cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    // ================= 文件 =================

    private sealed class PluginFilesApi : IPluginFiles
    {
        private readonly PluginHostBridge _owner;
        public PluginFilesApi(PluginHostBridge owner) => _owner = owner;

        private RemoteFileSystem? Fs => _owner._vm.FileSystem;

        public string CurrentDirectory => _owner._vm.CurrentPath;

        public PluginTarget? SelectedTarget => _owner._vm.PluginSelection.FirstOrDefault();

        public IReadOnlyList<PluginTarget> SelectedTargets => _owner._vm.PluginSelection;

        public async Task<IReadOnlyList<PluginFileEntry>> ListAsync(string remotePath,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            if (fs == null) return Array.Empty<PluginFileEntry>();
            var list = await fs.ListAsync(remotePath, cancellationToken).ConfigureAwait(false);
            return list.Select(e => new PluginFileEntry(
                e.Name, e.FullPath, e.IsDir, e.Size, e.Modified, e.Perms, e.Owner, e.Selinux, e.LinkTarget)).ToList();
        }

        public async Task<bool> ExistsAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            return fs != null && await fs.ExistsAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }

        public async Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            var fs = Fs ?? throw new InvalidOperationException("未连接设备");
            await fs.MkdirAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }

        public async Task DeleteAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            var fs = Fs ?? throw new InvalidOperationException("未连接设备");
            await fs.DeleteAsync(new[] { remotePath }, cancellationToken).ConfigureAwait(false);
        }

        public async Task RenameAsync(string remotePath, string newName,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs ?? throw new InvalidOperationException("未连接设备");
            await fs.RenameAsync(remotePath, newName, cancellationToken).ConfigureAwait(false);
        }

        public async Task<byte[]> ReadAllBytesAsync(string remotePath,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs ?? throw new InvalidOperationException("未连接设备");
            return await fs.ReadBytesAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteAllBytesAsync(string remotePath, byte[] data,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs ?? throw new InvalidOperationException("未连接设备");
            if (!await fs.WriteBytesAsync(remotePath, data ?? Array.Empty<byte>(), cancellationToken)
                         .ConfigureAwait(false))
                throw new IOException("写入失败（请检查 root 授权）");
        }

        public async Task<string> ReadAllTextAsync(string remotePath,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs ?? throw new InvalidOperationException("未连接设备");
            return await fs.ReadTextAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteAllTextAsync(string remotePath, string text,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs ?? throw new InvalidOperationException("未连接设备");
            await fs.WriteTextAsync(remotePath, text ?? "", cancellationToken).ConfigureAwait(false);
        }

        public async Task DownloadAsync(string remotePath, string localPath,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs ?? throw new InvalidOperationException("未连接设备");
            await fs.DownloadAsync(remotePath, localPath, cancellationToken).ConfigureAwait(false);
        }

        public async Task UploadAsync(string localPath, string remoteDirectory,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs ?? throw new InvalidOperationException("未连接设备");
            await fs.UploadAsync(localPath, remoteDirectory, cancellationToken).ConfigureAwait(false);
        }

        public async Task SetPermissionsAsync(string remotePath, string octal,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs ?? throw new InvalidOperationException("未连接设备");
            await fs.ChmodAsync(remotePath, octal, cancellationToken).ConfigureAwait(false);
        }
    }

    // ================= 应用 =================

    private sealed class PluginAppsApi : IPluginApps
    {
        private readonly PluginHostBridge _owner;
        public PluginAppsApi(PluginHostBridge owner) => _owner = owner;

        private RemoteFileSystem? Fs => _owner._vm.FileSystem;

        public async Task<IReadOnlyList<PluginApp>> ListAsync(bool systemApps,
            CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            if (fs == null) return Array.Empty<PluginApp>();
            var list = await new AppService(fs).ListAsync(systemApps, cancellationToken).ConfigureAwait(false);
            return list.Select(a => new PluginApp(a.Package, string.IsNullOrWhiteSpace(a.Label) ? a.Package : a.Label,
                a.IsSystem, a.ApkPath, 0, false)).ToList();
        }

        public async Task<PluginApp?> GetAsync(string package, CancellationToken cancellationToken = default)
        {
            var fs = Fs;
            if (fs == null || string.IsNullOrWhiteSpace(package)) return null;

            var raw = await fs.RunCommandAsync("pm list packages -f " + PluginShell.Quote(package), false,
                cancellationToken).ConfigureAwait(false);
            var apk = "";
            var found = false;
            foreach (var line in raw.Split('\n'))
            {
                var t = line.Trim();
                if (!t.StartsWith("package:", StringComparison.Ordinal)) continue;
                var body = t[8..];
                var eq = body.LastIndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(body[(eq + 1)..].Trim(), package, StringComparison.Ordinal)) continue;
                apk = body[..eq].Trim();
                found = true;
                break;
            }
            if (!found) return null;

            var service = new AppService(fs);
            var label = await service.GetLabelAsync(package, apk, cancellationToken).ConfigureAwait(false);
            var uidRaw = await fs.RunCommandAsync(
                "stat -c %u " + PluginShell.Quote(DevicePaths.UserData(package)) + " 2>/dev/null", true,
                cancellationToken).ConfigureAwait(false);
            var uid = 0;
            foreach (var line in uidRaw.Split('\n'))
            {
                var t = line.Trim();
                if (int.TryParse(t, out var parsed)) { uid = parsed; break; }
            }

            var isSystem = false;
            try
            {
                var sys = await fs.RunCommandAsync("pm list packages -s " + PluginShell.Quote(package), false,
                    cancellationToken).ConfigureAwait(false);
                isSystem = sys.Contains(package, StringComparison.Ordinal);
            }
            catch { }

            var granted = false;
            try
            {
                var root = _owner._vm.RootProvider;
                if (root != null)
                {
                    var grants = await root.ReadGrantsAsync(cancellationToken).ConfigureAwait(false);
                    granted = grants.Any(g => string.Equals(g.Package, package, StringComparison.Ordinal) && g.Allowed);
                }
            }
            catch { }

            return new PluginApp(package, string.IsNullOrWhiteSpace(label) ? package : label,
                isSystem, apk, uid, granted);
        }

        public async Task<bool> SetRootGrantAsync(string package, bool allowed,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(package)) return false;
            return await OnUiAsync(async () =>
            {
                var item = new AppItem { Package = package, Label = package };
                await _owner._vm.ToggleRootAsync(item, allowed).ConfigureAwait(true);
                return item.IsGranted == allowed;
            }).ConfigureAwait(true);
        }

        public Task<bool> InstallApkAsync(string localApkPath, CancellationToken cancellationToken = default)
            => _owner._vm.InstallApkAsync(localApkPath, cancellationToken);   // 插件取消 = 真的中止 adb install

        public async Task<bool> UninstallAsync(string package, bool keepData = false,
            CancellationToken cancellationToken = default)
        {
            var app = await GetAsync(package, cancellationToken).ConfigureAwait(false);
            if (app == null) return false;
            return await OnUiAsync(async () =>
            {
                var item = new AppItem
                {
                    Package = app.Package,
                    Label = app.Label,
                    IsSystem = app.IsSystem,
                    ApkPath = app.ApkPath,
                };
                return await _owner._vm.UninstallAppAsync(item).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }

        public async Task<bool> ExtractApkAsync(string package, string localApkPath,
            CancellationToken cancellationToken = default)
        {
            var app = await GetAsync(package, cancellationToken).ConfigureAwait(false);
            if (app == null || string.IsNullOrEmpty(app.ApkPath)) return false;
            return await OnUiAsync(async () =>
            {
                var item = new AppItem
                {
                    Package = app.Package,
                    Label = app.Label,
                    IsSystem = app.IsSystem,
                    ApkPath = app.ApkPath,
                };
                return await _owner._vm.ExtractApkToPcAsync(item, localApkPath).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
    }

    // ================= 模块 =================

    private sealed class PluginModulesApi : IPluginModules
    {
        private readonly PluginHostBridge _owner;
        public PluginModulesApi(PluginHostBridge owner) => _owner = owner;

        public async Task<IReadOnlyList<PluginModule>> ListAsync(CancellationToken cancellationToken = default)
        {
            return await OnUiAsync(async () =>
            {
                await _owner._vm.LoadModulesAsync().ConfigureAwait(true);
                return (IReadOnlyList<PluginModule>)_owner._vm.Modules.Select(m => new PluginModule(
                    m.Id, m.Name, m.Version, m.Author, m.Description, m.Dir,
                    m.Enabled, m.HasWebUI, m.HasAction)).ToList();
            }).ConfigureAwait(true);
        }

        public async Task<bool> InstallAsync(string localZipPath, Action<string>? log = null,
            CancellationToken cancellationToken = default)
        {
            return await OnUiAsync(async () =>
                await _owner._vm.InstallModuleZipAsync(localZipPath, s => log?.Invoke(s)).ConfigureAwait(true))
                .ConfigureAwait(true);
        }

        public async Task<bool> SetEnabledAsync(string moduleId, bool enabled,
            CancellationToken cancellationToken = default)
        {
            return await OnUiAsync(async () =>
            {
                var module = _owner._vm.Modules.FirstOrDefault(m =>
                    string.Equals(m.Id, moduleId, StringComparison.OrdinalIgnoreCase));
                if (module == null) return false;
                return await _owner._vm.SetModuleEnabledAsync(module, enabled).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }

        public async Task<bool> UninstallAsync(string moduleId, CancellationToken cancellationToken = default)
        {
            return await OnUiAsync(async () =>
            {
                var module = _owner._vm.Modules.FirstOrDefault(m =>
                    string.Equals(m.Id, moduleId, StringComparison.OrdinalIgnoreCase));
                if (module == null) return false;
                // 以前无条件 return true：VM 把异常咽进 StatusText，插件永远以为卸载成功。
                return await _owner._vm.UninstallModuleAsync(module).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }

        public async Task<string> RunActionAsync(string moduleId, CancellationToken cancellationToken = default)
        {
            return await OnUiAsync(async () =>
            {
                var module = _owner._vm.Modules.FirstOrDefault(m =>
                    string.Equals(m.Id, moduleId, StringComparison.OrdinalIgnoreCase));
                return module == null ? "模块不存在" : await _owner._vm.RunModuleActionAsync(module).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
    }

    // ================= 设置 / 日志 / 事件 =================

    private sealed class PluginSettingsStore : IPluginSettings
    {
        private readonly string _path;
        private readonly object _lock = new();
        private Dictionary<string, JsonElement> _map = new(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;

        public PluginSettingsStore(string path)
        {
            _path = path;
            Load();
        }

        private void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(_path)) return;
                var json = File.ReadAllText(_path);
                var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (map != null) _map = new Dictionary<string, JsonElement>(map, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        public T? Get<T>(string key, T? defaultValue = default)
        {
            lock (_lock)
            {
                Load();
                if (!_map.TryGetValue(key, out var element)) return defaultValue;
                try { return element.Deserialize<T>(); }
                catch { return defaultValue; }
            }
        }

        public void Set<T>(string key, T value)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            lock (_lock)
            {
                Load();
                _map[key] = JsonSerializer.SerializeToElement(value);
            }
            Save();
        }

        public bool Remove(string key)
        {
            lock (_lock)
            {
                Load();
                var removed = _map.Remove(key);
                if (removed) Save();
                return removed;
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    // 原子写：插件设置写到一半崩溃/断电，不能留下半截 JSON
                    AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_map,
                        new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { }
            }
        }
    }

    private sealed class PluginLogApi : IPluginLog
    {
        private readonly PluginHostBridge _owner;
        private readonly object _lock = new();

        public PluginLogApi(PluginHostBridge owner) => _owner = owner;

        public void Info(string message) => Write("INFO", message, null);

        public void Warn(string message) => Write("WARN", message, null);

        public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

        public void Debug(string message)
        {
            if (!AppEnvironment.PluginDebug) return;
            Write("DEBUG", message, null);
        }

        private void Write(string level, string message, Exception? exception)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
            if (exception != null) line += Environment.NewLine + exception;

            lock (_lock)
            {
                try
                {
                    var dir = AppPaths.Ensure(AppPaths.PluginLogs);
                    var safe = _owner.Metadata.Id;
                    foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                    File.AppendAllText(Path.Combine(dir, safe + ".log"), line + Environment.NewLine);
                }
                catch { }
            }

            _owner._vm.AppendLog($"[{_owner.Metadata.Name}] {message}");
        }
    }

    private sealed class PluginEvents : IPluginEvents
    {
        public event EventHandler<DeviceSnapshot>? DeviceChanged;
        public event EventHandler<PluginTarget?>? FileSelectionChanged;

        public void RaiseDeviceChanged(DeviceSnapshot snapshot)
            => DeviceChanged?.Invoke(this, snapshot);

        public void RaiseFileSelection(PluginTarget? target)
            => FileSelectionChanged?.Invoke(this, target);
    }
}
