using System;
using System.IO;

namespace DashFile.Services;

/// <summary>
/// 本机（Windows）侧的固定目录。以前这些路径散在 AppService / SuperuserService /
/// PluginHostBridge / MainViewModel 里，换目录要点很多处；现在统一从这里取。
/// </summary>
public static class AppPaths
{
    private static readonly string LocalRootCached = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DashFile");

    /// <summary>%LOCALAPPDATA%\DashFile</summary>
    public static string LocalRoot => LocalRootCached;

    public static readonly string Icons = Path.Combine(LocalRootCached, "icons");
    public static readonly string Su = Path.Combine(LocalRootCached, "su");
    public static readonly string Labels = Path.Combine(LocalRootCached, "labels.json");
    public static readonly string Settings = Path.Combine(LocalRootCached, "settings.json");
    public static readonly string Plugins = Path.Combine(LocalRootCached, "plugins");

    /// <summary>插件数据（设置/缓存）。必须在扫描根之外，否则卸载插件时无法保留数据</summary>
    public static readonly string PluginData = Path.Combine(LocalRootCached, "plugin-data");
    public static readonly string PluginLogs = Path.Combine(LocalRootCached, "plugin-logs");

    public static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }
}
