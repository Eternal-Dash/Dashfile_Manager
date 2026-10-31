using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DashFile.Services.Root;

/// <summary>
/// 一次 Root 识别过程中的共享上下文：设备文件系统、证据、包版本缓存。
/// provider 工厂只依赖这个上下文，不再各自持有 MainViewModel / AdbRunner。
/// </summary>
public sealed class RootDetectionContext
{
    private readonly Dictionary<string, string> _packageVersions = new(StringComparer.OrdinalIgnoreCase);

    public RootDetectionContext(RemoteFileSystem fileSystem, RootEvidence evidence)
    {
        FileSystem = fileSystem;
        Evidence = evidence;
    }

    public RemoteFileSystem FileSystem { get; }

    public RootEvidence Evidence { get; }

    /// <summary>读取管理器 App 的 versionName；同一个包只查一次。</summary>
    public async Task<string> PackageVersionAsync(string package)
    {
        if (string.IsNullOrWhiteSpace(package)) return "";
        if (_packageVersions.TryGetValue(package, out var cached)) return cached;

        var version = "";
        try
        {
            var raw = await FileSystem.RunCommandAsync(
                $"dumpsys package {Sh.Q(package)} 2>/dev/null | grep -m1 versionName", false)
                .ConfigureAwait(false);
            var match = Regex.Match(raw, @"versionName=(\S+)");
            if (match.Success) version = match.Groups[1].Value;
        }
        catch { }

        _packageVersions[package] = version;
        return version;
    }
}

/// <summary>
/// Root 实现工厂。默认注册在 <see cref="RootProviderRegistry"/> 里，按注册顺序尝试；
/// 新增一种管理器 = 新增一个工厂并注册，不需要改 DetectAsync 的 if/else。
/// </summary>
public interface IRootProviderFactory
{
    Task<IRootProvider?> TryCreateAsync(RootDetectionContext context);
}
