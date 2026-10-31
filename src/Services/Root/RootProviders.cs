using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DashFile.Services.Root;

/// <summary>
/// Root 证据收集 + 识别入口。
///
/// 判定顺序完全由 <see cref="RootProviderRegistry"/> 的工厂顺序决定：
///   KSU 系 → APatch → Magisk 系 → SuperSU → phh → Lineage → Unknown。
///
/// 识别不靠包名硬猜，而是收集设备证据（一次 root shell）：
///   su -v、.allowlist 的 USK 指纹、ksud / magisk.db / magisk / apd、
///   /proc/modules、管理器包名。包名只用于显示名、版本、是否装了管理器。
/// </summary>
public static class RootProviders
{
    private static readonly string ProbeScript = string.Join(" ",
        "SU=$(command -v su 2>/dev/null);",
        "V=\"\"; [ -n \"$SU\" ] && V=$($SU -v 2>/dev/null | head -1);",

        // KernelSU 系
        "KSUD=$(command -v ksud 2>/dev/null);",
        $"[ -z \"$KSUD\" ] && [ -x {DevicePaths.AdbData}/ksud ] && KSUD={DevicePaths.AdbData}/ksud;",
        $"[ -z \"$KSUD\" ] && [ -x {DevicePaths.KsuDir}/bin/ksud ] && KSUD={DevicePaths.KsuDir}/bin/ksud;",
        "KSUD_V=\"\"; KSUD_INFO=\"\";",
        "[ -n \"$KSUD\" ] && KSUD_V=$($KSUD -V 2>/dev/null | head -1);",
        "[ -n \"$KSUD\" ] && KSUD_INFO=$($KSUD debug info 2>/dev/null | sed -n \"s/^version: *//p\" | head -1);",

        // Magisk 系
        "MAGISK=$(command -v magisk 2>/dev/null);",
        $"[ -z \"$MAGISK\" ] && [ -x {DevicePaths.MagiskDir}/magisk ] && MAGISK={DevicePaths.MagiskDir}/magisk;",
        $"[ -z \"$MAGISK\" ] && [ -x {DevicePaths.AdbData}/magisk64 ] && MAGISK={DevicePaths.AdbData}/magisk64;",
        "MAGISK_V=\"\"; MAGISK_C=\"\";",
        "[ -n \"$MAGISK\" ] && MAGISK_V=$($MAGISK -v 2>/dev/null | head -1);",
        "[ -n \"$MAGISK\" ] && MAGISK_C=$($MAGISK -V 2>/dev/null | head -1);",

        // APatch
        "APD=$(command -v apd 2>/dev/null);",
        $"[ -z \"$APD\" ] && [ -x {DevicePaths.AdbData}/apd ] && APD={DevicePaths.AdbData}/apd;",
        $"[ -z \"$APD\" ] && [ -x {DevicePaths.APatchDir}/bin/apd ] && APD={DevicePaths.APatchDir}/bin/apd;",
        "APD_V=\"\"; [ -n \"$APD\" ] && APD_V=$($APD -V 2>/dev/null | head -1);",

        // 目录 / 内核模块 / 传统实现痕迹
        $"KSU_DIR=0; [ -d {DevicePaths.KsuDir} ] && KSU_DIR=1;",
        $"MAGDB=0; [ -f {DevicePaths.MagiskDb} ] && MAGDB=1;",
        $"AP_DIR=0; [ -d {DevicePaths.APatchDir} ] && AP_DIR=1;",
        $"MODULES=0; [ -d {DevicePaths.Modules} ] && MODULES=1;",
        "PROC=$(grep -E '^(kernelsu|ksu|apatch|magisk)' /proc/modules 2>/dev/null | cut -d' ' -f1 | tr '\\n' ',');",
        $"SUPERSU=0; [ -e {DevicePaths.SuperSuConfig} ] && SUPERSU=1; " +
        $"[ -d {RootPackages.ManagerDataDir(RootPackages.SuperSu)} ] && SUPERSU=1;",
        $"PHH=0; [ -d {RootPackages.ManagerDataDir(RootPackages.PhhSuperuser)} ] && PHH=1;",
        $"LINEAGE=0; [ -x {DevicePaths.SystemSu} ] && LINEAGE=1;",

        // 输出
        "echo \"S=$SU\"; echo \"V=$V\";",
        "echo \"KSU_DIR=$KSU_DIR\";",
        $"echo \"ALLOW=$(head -c 3 {DevicePaths.KsuAllowlist} 2>/dev/null)\";",
        "echo \"KSUD=$KSUD\"; echo \"KSUD_V=$KSUD_V\"; echo \"KSUD_INFO=$KSUD_INFO\";",
        "echo \"MAGDB=$MAGDB\";",
        "echo \"MAGISK=$MAGISK\"; echo \"MAGISK_V=$MAGISK_V\"; echo \"MAGISK_C=$MAGISK_C\";",
        "echo \"AP_DIR=$AP_DIR\";",
        "echo \"APD=$APD\"; echo \"APD_V=$APD_V\";",
        "echo \"MODULES=$MODULES\";",
        "echo \"PROC=$PROC\";",
        "echo \"SUPERSU=$SUPERSU\"; echo \"PHH=$PHH\"; echo \"LINEAGE=$LINEAGE\";",
        "echo \"PKGS_BEGIN\"; pm list packages 2>/dev/null; echo \"PKGS_END\"");

    public static async Task<RootEvidence> CollectAsync(RemoteFileSystem fs, CancellationToken ct = default)
    {
        var raw = "";
        try { raw = await fs.RunCommandAsync(ProbeScript, true, ct).ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Add("Root 探测失败：" + ex.Message); }

        // 一次扫描成字典，避免对每个字段单独跑正则（原来 10 次 Regex.Match）
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.Trim();
            var index = line.IndexOf('=');
            if (index <= 0) continue;
            var key = line[..index].Trim();
            if (key.Length is > 0 and <= 24) fields[key] = line[(index + 1)..].Trim();
        }

        string Get(string key)
            => fields.TryGetValue(key, out var value) ? value : "";

        var pkgStart = raw.IndexOf("PKGS_BEGIN", StringComparison.Ordinal);
        var pkgEnd = raw.IndexOf("PKGS_END", StringComparison.Ordinal);
        var packages = pkgStart >= 0 && pkgEnd > pkgStart
            ? raw[(pkgStart + "PKGS_BEGIN".Length)..pkgEnd]
            : "";

        var evidence = new RootEvidence
        {
            SuPath = Get("S"),
            SuVersion = Get("V"),
            AllowlistMagic = Get("ALLOW"),
            HasKsuDir = Get("KSU_DIR") == "1",
            KsudPath = Get("KSUD"),
            KsudVersion = Get("KSUD_V"),
            KsudInfoVersion = Get("KSUD_INFO"),
            HasMagiskDb = Get("MAGDB") == "1",
            MagiskPath = Get("MAGISK"),
            MagiskVersion = Get("MAGISK_V"),
            MagiskVersionCode = Get("MAGISK_C"),
            HasApDir = Get("AP_DIR") == "1",
            ApdPath = Get("APD"),
            ApatchVersion = Get("APD_V"),
            HasModulesDir = Get("MODULES") == "1",
            KernelModules = Get("PROC"),
            HasSuperSuFiles = Get("SUPERSU") == "1",
            HasPhhFiles = Get("PHH") == "1",
            HasLineageSu = Get("LINEAGE") == "1",
            PackageList = packages,
        };
        evidence.FillPackages();
        return evidence;
    }

    public static async Task<IRootProvider?> DetectAsync(RemoteFileSystem fs, CancellationToken ct = default)
    {
        var evidence = await CollectAsync(fs, ct).ConfigureAwait(false);
        var context = new RootDetectionContext(fs, evidence);

        foreach (var factory in RootProviderRegistry.Factories)
        {
            var provider = await factory.TryCreateAsync(context).ConfigureAwait(false);
            if (provider != null) return provider;
        }
        return null;
    }
}
