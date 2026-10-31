using System;
using System.IO;

namespace DashFile.Services;

/// <summary>
/// 设备（Android）侧的固定路径与临时文件命名。
///
/// 为什么集中：/data/adb、/data/local/tmp、/sdcard 这些字符串以前在 Root provider、
/// ModuleService、RemoteFileSystem、MainViewModel、插件桥里各写一遍，改一处漏一处。
/// 临时文件统一用 dashfile_/dashfile_plugin_ 前缀，退出清理才不会漏。
/// </summary>
public static class DevicePaths
{
    public const string Sdcard = "/sdcard";
    public const string Download = "/sdcard/Download";
    public const string Dcim = "/sdcard/DCIM";
    public const string SdcardAndroid = "/sdcard/Android";

    public const string AdbData = "/data/adb";
    public const string Modules = "/data/adb/modules";
    public const string ModulesUpdate = "/data/adb/modules_update";
    public const string KsuDir = "/data/adb/ksu";
    public const string KsuAllowlist = "/data/adb/ksu/.allowlist";
    public const string KsuAllowlistAlt = "/data/adb/ksu/allowlist";
    public const string KsuBusybox = "/data/adb/ksu/bin/busybox";
    public const string MagiskDir = "/data/adb/magisk";
    public const string MagiskDb = "/data/adb/magisk.db";
    public const string MagiskBusybox = "/data/adb/magisk/busybox";
    public const string APatchDir = "/data/adb/ap";
    public const string ApdBusybox = "/data/adb/ap/bin/busybox";

    public const string LocalTmp = "/data/local/tmp";
    public const string DataRoot = "/data/data";
    public const string UserDataRoot = "/data/user/0";
    public const string UserDeDataRoot = "/data/user_de/0";
    public const string SystemBin = "/system/bin";
    public const string SystemSu = "/system/bin/su";
    public const string SuperSuConfig = "/system/etc/.supersu";
    public const string Dropbox = "/data/system/dropbox";
    public const string Tombstones = "/data/tombstones";
    public const string AdbKeys = "/data/misc/adb/adb_keys";
    public const string AdbTempKeys = "/data/misc/adb/adb_temp_keys.xml";

    public const string TempPrefix = ".dashfile_";
    public const string PluginTempPrefix = ".dashfile_plugin_";

    public static string ModuleDir(string moduleId) => Modules + "/" + moduleId;
    public static string ModuleUpdateDir(string moduleId) => ModulesUpdate + "/" + moduleId;
    public static string UserData(string package) => UserDataRoot + "/" + package;
    public static string UserDeData(string package) => UserDeDataRoot + "/" + package;

    /// <summary>设备端临时文件路径，例如 /data/local/tmp/.dashfile_mod_ab12cd34.zip</summary>
    public static string TempFile(string tag, string extension = "")
        => LocalTmp + "/" + TempPrefix + tag + "_" + Guid.NewGuid().ToString("N")[..12] + extension;

    /// <summary>设备端临时目录路径（调用方负责 mkdir）</summary>
    public static string TempDirectory(string tag)
        => LocalTmp + "/" + TempPrefix + tag + "_" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>本机临时文件名（用于 adb push 的中转）</summary>
    public static string LocalTempFile(string tag, string extension = "")
        => Path.Combine(Path.GetTempPath(), "dashfile_" + tag + "_" + Guid.NewGuid().ToString("N") + extension);
}
