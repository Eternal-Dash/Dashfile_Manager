using System;

namespace DashFile.Services;

/// <summary>
/// 调试 / 启动参数集中收口。以前 "DASHFILE_PERF"、"DASHFILE_START_TAB" 等
/// 字符串散在 4 个文件里，改名字要全局搜；现在只在这里定义。
/// </summary>
public static class AppEnvironment
{
    public const string PerfVariable = "DASHFILE_PERF";
    public const string StartTabVariable = "DASHFILE_START_TAB";
    public const string StartPathVariable = "DASHFILE_START_PATH";
    public const string IconDebugVariable = "DASHFILE_ICON_DEBUG";
    public const string PluginDebugVariable = "DASHFILE_PLUGIN_DEBUG";
    public const string AppModeVariable = "DASHFILE_APP_MODE";

    public static readonly bool Perf = IsOn(PerfVariable);
    public static readonly bool IconDebug = IsOn(IconDebugVariable);
    public static readonly bool PluginDebug = IsOn(PluginDebugVariable);

    public static readonly int? StartTab = ParseStartTab();
    public static readonly int? AppMode = ParseAppMode();
    public static readonly string StartPath = Environment.GetEnvironmentVariable(StartPathVariable) ?? "";

    private static bool IsOn(string name)
        => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);

    private static int? ParseStartTab()
        => int.TryParse(Environment.GetEnvironmentVariable(StartTabVariable), out var value) &&
           value is >= 0 and <= 5
            ? value
            : null;

    private static int? ParseAppMode()
        => int.TryParse(Environment.GetEnvironmentVariable(AppModeVariable), out var value) &&
           value is >= 0 and <= 2
            ? value
            : null;
}
