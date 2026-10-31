using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

namespace DashFile.PluginApi;

/// <summary>插件可以作用的目标类型。</summary>
public enum PluginTargetKind
{
    None = 0,
    File = 1,
    Directory = 2,
    App = 3,
    Module = 4,
    Device = 5,
    /// <summary>任何目标，通常用于插件页里的全局工具。</summary>
    Any = 100,
}

/// <summary>
/// 一次插件动作的上下文。不同 Kind 只填对应字段，其余为空字符串。
/// </summary>
public sealed class PluginTarget
{
    public PluginTargetKind Kind { get; init; }

    /// <summary>文件 / 目录在设备上的绝对路径。</summary>
    public string Path { get; init; } = "";

    /// <summary>应用包名。</summary>
    public string Package { get; init; } = "";

    /// <summary>模块 id（/data/adb/modules 下的目录名）。</summary>
    public string ModuleId { get; init; } = "";

    /// <summary>显示用名称（文件名、应用名、模块名）。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>附加数据，留给插件自己解释（例如多选时的 JSON）。</summary>
    public string? Extra { get; init; }

    public static PluginTarget ForFile(string path, string? displayName = null) => new()
    {
        Kind = PluginTargetKind.File,
        Path = path,
        DisplayName = displayName ?? System.IO.Path.GetFileName(path),
    };

    public static PluginTarget ForDirectory(string path, string? displayName = null) => new()
    {
        Kind = PluginTargetKind.Directory,
        Path = path,
        DisplayName = displayName ?? path,
    };

    public static PluginTarget ForApp(string package, string? displayName = null, string apkPath = "") => new()
    {
        Kind = PluginTargetKind.App,
        Package = package,
        DisplayName = displayName ?? package,
        Extra = apkPath,
    };

    public static PluginTarget ForModule(string moduleId, string? displayName = null, string moduleDir = "") => new()
    {
        Kind = PluginTargetKind.Module,
        ModuleId = moduleId,
        DisplayName = displayName ?? moduleId,
        Extra = moduleDir,
    };

    public static PluginTarget ForDevice(string displayName = "") => new()
    {
        Kind = PluginTargetKind.Device,
        DisplayName = displayName,
    };
}

/// <summary>
/// 插件向文件 / 应用 / 模块的右键菜单注入的动作。
/// 动作在 UI 线程之外执行也可以，但插件必须自己把 UI 操作切回 UI 线程（用 <see cref="IPluginUi"/>）。
/// </summary>
public sealed class PluginAction
{
    /// <summary>插件内唯一 id。</summary>
    public string Id { get; init; } = "";

    /// <summary>菜单里显示的文字。</summary>
    public string Title { get; init; } = "";

    /// <summary>可选说明，作为 ToolTip。</summary>
    public string? Description { get; init; }

    /// <summary>可选菜单图标：16×16 坐标系下的 StreamGeometry 路径（描边风格）。为空时宿主用默认拼图图标。</summary>
    public string? Icon { get; init; }

    /// <summary>在哪些目标上出现。</summary>
    public PluginTargetKind[] Targets { get; init; } = new[] { PluginTargetKind.Any };

    /// <summary>是否需要 root；没有 root 时宿主会置灰或提示。</summary>
    public bool RequiresRoot { get; init; }

    /// <summary>是否显示在右键菜单；false 时只作为插件页里的全局动作。</summary>
    public bool ShowInContextMenu { get; init; } = true;

    /// <summary>可选的可用性判断；返回 false 时菜单项置灰。</summary>
    public Func<PluginTarget, bool>? CanExecute { get; init; }

    /// <summary>真正执行的动作。</summary>
    public Func<PluginTarget, CancellationToken, Task> ExecuteAsync { get; init; } = (_, _) => Task.CompletedTask;
}

/// <summary>
/// 插件提供的一个页面。宿主会在“插件”页里列出，并在右侧 ContentControl 中显示返回的控件。
/// 创建控件必须发生在 UI 线程；宿主调用 CreateViewAsync 时已经在 UI 线程。
/// </summary>
public sealed class PluginPage
{
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    public string? Description { get; init; }

    /// <summary>可选图标文字（建议一个 emoji 或 1~2 个汉字）。</summary>
    public string? Icon { get; init; }

    /// <summary>创建页面控件。可以每次创建新控件，宿主会缓存当前页。</summary>
    public Func<IPluginHost, CancellationToken, Task<Control>> CreateViewAsync { get; init; }
        = (_, _) => Task.FromResult<Control>(new TextBlock { Text = "（插件没有提供页面）" });

    /// <summary>简单页面的快捷写法。</summary>
    public static PluginPage FromControl(string id, string title, Func<IPluginHost, Control> factory,
        string? description = null, string? icon = null)
        => new()
        {
            Id = id,
            Title = title,
            Description = description,
            Icon = icon,
            CreateViewAsync = (host, _) => Task.FromResult(factory(host)),
        };
}

/// <summary>设备端 shell 执行结果。</summary>
public sealed record PluginShellResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    public string All => (StdOut ?? "") + (StdErr ?? "");
}

/// <summary>设备上的一个文件 / 目录条目。</summary>
public sealed record PluginFileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime Modified,
    string Permissions,
    string Owner,
    string Selinux,
    string LinkTarget);

/// <summary>应用条目快照。</summary>
public sealed record PluginApp(
    string Package,
    string Label,
    bool IsSystem,
    string ApkPath,
    int Uid,
    bool IsRootGranted);

/// <summary>模块条目快照。</summary>
public sealed record PluginModule(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    string Directory,
    bool Enabled,
    bool HasWebUi,
    bool HasAction);

/// <summary>设备连接快照，随 DeviceChanged 事件发送。</summary>
public sealed record DeviceSnapshot(
    bool IsConnected,
    bool IsRoot,
    string Serial,
    string Model,
    string AndroidVersion,
    string RootManager);

/// <summary>
/// 宿主传给插件的全部能力入口。
/// 插件通常只在自己的 Initialize 里调用 AddPage / AddAction，然后保存 host 供后续使用。
/// </summary>
public interface IPluginHost
{
    /// <summary>当前插件元信息。</summary>
    PluginMetadata Metadata { get; }

    /// <summary>宿主版本，例如 1.0.0-alpha1。</summary>
    string HostVersion { get; }

    /// <summary>该插件专属的本机数据目录（已创建），可以放缓存、下载文件。</summary>
    string DataDirectory { get; }

    IPluginUi Ui { get; }

    IPluginDevice Device { get; }

    IPluginFiles Files { get; }

    IPluginApps Apps { get; }

    IPluginModules Modules { get; }

    IPluginSettings Settings { get; }

    IPluginLog Log { get; }

    IPluginEvents Events { get; }

    /// <summary>注册一个插件页面（显示在“插件”页右侧）。</summary>
    void AddPage(PluginPage page);

    /// <summary>注册一个右键菜单动作。</summary>
    void AddAction(PluginAction action);

    /// <summary>注册一个全局动作；显示在插件页里，不依赖当前选中的文件/应用。</summary>
    void AddGlobalAction(PluginAction action);

    /// <summary>
    /// 更新"插件页右栏顶部"（插件名右侧）显示的信息：副标题 / 徽标 / 状态。
    /// 宿主的页面外壳已经显示了插件名与版本，插件不必在页面里再画一遍标题卡片；
    /// 想要展示的能力标签（"无 Python 依赖"之类）和运行状态从这里推给宿主即可。
    /// 可在任意线程调用，宿主会切到 UI 线程更新。
    /// </summary>
    void SetHeader(PluginHeaderInfo? header);

    /// <summary>只更新状态文本（例如"就绪"/"解析中…"），副标题与徽标保持上一次的值。</summary>
    void SetStatus(string? status);
}

/// <summary>
/// 插件页顶部展示的附加信息（由 <see cref="IPluginHost.SetHeader"/> 推送）。
/// </summary>
public sealed record PluginHeaderInfo
{
    /// <summary>副标题：一句话说明这个插件页做什么。</summary>
    public string? Subtitle { get; init; }

    /// <summary>徽标：能力标签，例如 "AOSP testkey 内置"、"无 Python 依赖"。</summary>
    public IReadOnlyList<string> Badges { get; init; } = System.Array.Empty<string>();

    /// <summary>状态：例如 "就绪 · 纯 C#"。右侧单行显示。</summary>
    public string? Status { get; init; }
}

/// <summary>插件可以调用的宿主 UI 能力。</summary>
public interface IPluginUi
{
    void Toast(string message);

    Task ShowMessageAsync(string title, string message, CancellationToken cancellationToken = default);

    Task<bool> ConfirmAsync(string title, string message, string okText = "确定",
        string cancelText = "取消", CancellationToken cancellationToken = default);

    Task<string?> PromptAsync(string title, string label, string initialValue = "",
        CancellationToken cancellationToken = default);

    /// <summary>打开本地文件选择框；patterns 例如 "*.apk"、"*.zip"。</summary>
    Task<string?> OpenFileAsync(string title, params string[] patterns);

    /// <summary>打开本地文件选择框并允许多选。</summary>
    Task<IReadOnlyList<string>> OpenFilesAsync(string title, params string[] patterns);

    /// <summary>打开本机保存对话框。</summary>
    Task<string?> SaveFileAsync(string title, string suggestedFileName, params string[] patterns);

    /// <summary>选择本机文件夹。</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>创建一个进度提示；Report 会更新宿主状态栏，Dispose 时结束。</summary>
    IPluginProgress CreateProgress(string title);

    void RunOnUiThread(Action action);

    Task RunOnUiThreadAsync(Action action);

    /// <summary>
    /// 覆盖一个主题资源。key 例如 Accent / PanelAlt / RadiusCard / RowH；
    /// value 可以是 Color、IBrush、CornerRadius、double 等。返回的句柄 Dispose 后恢复原值。
    /// 必须在 UI 线程生效；后台调用会通过宿主调度。
    /// </summary>
    IDisposable OverrideResource(string key, object value);

    /// <summary>覆盖一个颜色资源（自动包成 SolidColorBrush）。</summary>
    IDisposable OverrideResourceColor(string key, Color color);

    /// <summary>清除本插件做过的所有资源覆盖，恢复宿主默认主题。</summary>
    void ClearResourceOverrides();
}

/// <summary>简单的进度回调。插件在后台线程调用 Report 即可。</summary>
public interface IPluginProgress : IProgress<string>, IDisposable
{
    string Title { get; }

    void Complete(string? message = null);
}

/// <summary>
/// 设备能力。所有方法都带超时；插件无需自己处理 su 授权弹窗导致的挂起（宿主有 20 秒兜底）。
/// </summary>
public interface IPluginDevice
{
    bool IsConnected { get; }

    bool IsRoot { get; }

    string Serial { get; }

    /// <summary>当前 root 实现名，例如 KernelSU / Magisk / APatch。</summary>
    string RootManagerName { get; }

    string Model { get; }

    string AndroidVersion { get; }

    /// <summary>执行 shell 命令（默认以 root 身份）。</summary>
    Task<PluginShellResult> ShellAsync(string command, bool root = true,
        CancellationToken cancellationToken = default);

    /// <summary>直接以 argv 执行一个设备端程序（不经过 shell，避免引号问题）。</summary>
    Task<PluginShellResult> ExecAsync(string executable, IReadOnlyList<string> arguments,
        bool root = true, CancellationToken cancellationToken = default);

    /// <summary>执行命令并把 stdout 当二进制取回。</summary>
    Task<byte[]> ExecCaptureAsync(string command, bool root = true,
        CancellationToken cancellationToken = default);

    Task<byte[]> ReadBytesAsync(string remotePath, CancellationToken cancellationToken = default);

    Task<bool> WriteBytesAsync(string remotePath, byte[] data,
        CancellationToken cancellationToken = default);

    Task<string> ReadTextAsync(string remotePath, CancellationToken cancellationToken = default);

    Task WriteTextAsync(string remotePath, string text, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string remotePath, CancellationToken cancellationToken = default);

    Task DownloadAsync(string remotePath, string localPath,
        CancellationToken cancellationToken = default);

    Task UploadAsync(string localPath, string remoteDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 把本机文件推到 /data/local/tmp 并返回远端临时路径（已 chmod 644）。
    /// 自动处理 adb push 只能以 shell 身份写文件的限制。
    /// </summary>
    Task<string> PushTempFileAsync(string localPath, CancellationToken cancellationToken = default);

    /// <summary>在 /data/local/tmp 下创建一个随机临时目录并返回路径。</summary>
    Task<string> CreateTempDirectoryAsync(CancellationToken cancellationToken = default);

    Task DeleteRemoteAsync(string remotePath, CancellationToken cancellationToken = default);
}

/// <summary>当前文件浏览上下文与设备文件读写。</summary>
public interface IPluginFiles
{
    /// <summary>文件页当前目录。</summary>
    string CurrentDirectory { get; }

    /// <summary>当前选中的文件 / 目录（多选时是第一项；没有选中为 null）。</summary>
    PluginTarget? SelectedTarget { get; }

    /// <summary>当前多选的目标列表。</summary>
    IReadOnlyList<PluginTarget> SelectedTargets { get; }

    Task<IReadOnlyList<PluginFileEntry>> ListAsync(string remotePath,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string remotePath, CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(string remotePath, CancellationToken cancellationToken = default);

    Task DeleteAsync(string remotePath, CancellationToken cancellationToken = default);

    Task RenameAsync(string remotePath, string newName, CancellationToken cancellationToken = default);

    Task<byte[]> ReadAllBytesAsync(string remotePath, CancellationToken cancellationToken = default);

    Task WriteAllBytesAsync(string remotePath, byte[] data,
        CancellationToken cancellationToken = default);

    Task<string> ReadAllTextAsync(string remotePath, CancellationToken cancellationToken = default);

    Task WriteAllTextAsync(string remotePath, string text,
        CancellationToken cancellationToken = default);

    Task DownloadAsync(string remotePath, string localPath,
        CancellationToken cancellationToken = default);

    Task UploadAsync(string localPath, string remoteDirectory,
        CancellationToken cancellationToken = default);

    Task SetPermissionsAsync(string remotePath, string octal,
        CancellationToken cancellationToken = default);
}

/// <summary>应用管理能力。</summary>
public interface IPluginApps
{
    Task<IReadOnlyList<PluginApp>> ListAsync(bool systemApps,
        CancellationToken cancellationToken = default);

    Task<PluginApp?> GetAsync(string package, CancellationToken cancellationToken = default);

    /// <summary>授予 / 取消 root 授权。返回 false 表示当前 root 实现不支持直接写授权表。</summary>
    Task<bool> SetRootGrantAsync(string package, bool allowed,
        CancellationToken cancellationToken = default);

    Task<bool> InstallApkAsync(string localApkPath, CancellationToken cancellationToken = default);

    Task<bool> UninstallAsync(string package, bool keepData = false,
        CancellationToken cancellationToken = default);

    Task<bool> ExtractApkAsync(string package, string localApkPath,
        CancellationToken cancellationToken = default);
}

/// <summary>模块管理能力。</summary>
public interface IPluginModules
{
    Task<IReadOnlyList<PluginModule>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>刷入本地 .zip 模块包；log 可能在后台线程回调。</summary>
    Task<bool> InstallAsync(string localZipPath, Action<string>? log = null,
        CancellationToken cancellationToken = default);

    Task<bool> SetEnabledAsync(string moduleId, bool enabled,
        CancellationToken cancellationToken = default);

    Task<bool> UninstallAsync(string moduleId, CancellationToken cancellationToken = default);

    Task<string> RunActionAsync(string moduleId, CancellationToken cancellationToken = default);
}

/// <summary>插件专属设置存储（JSON，自动保存）。</summary>
public interface IPluginSettings
{
    T? Get<T>(string key, T? defaultValue = default);

    void Set<T>(string key, T value);

    bool Remove(string key);

    void Save();
}

/// <summary>插件日志。写入本机插件日志文件，同时出现在宿主运行日志里。</summary>
public interface IPluginLog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);

    void Debug(string message);
}

/// <summary>宿主事件。事件在 UI 线程触发。</summary>
public interface IPluginEvents
{
    event EventHandler<DeviceSnapshot>? DeviceChanged;

    /// <summary>文件页选中项变化；没有选中时为 null。</summary>
    event EventHandler<PluginTarget?>? FileSelectionChanged;
}
