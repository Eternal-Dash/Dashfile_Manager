using System;
using System.Collections.Generic;
using System.Linq;

namespace DashFile.Services.Root;

/// <summary>
/// 识别结果的全部证据（一次 root shell 调用收集）。
///
/// 为什么不能只看包名：KernelSU 的改版（KernelSU / KSU-Next / SukiSU / ReSukiSU / KowSU / 私有 fork）
/// 包名千奇百怪，甚至可以不装管理器 App；Magisk 也有官方 / Alpha / Kitsune 多个包名。
/// 所以判定以“设备上的客观痕迹”为主，包名只用来取显示名和版本。
/// </summary>
public sealed record RootEvidence
{
    public string SuPath { get; init; } = "";

    /// <summary>su -v 的输出，例如 "3.3.1:KernelSU" / "27.0:MAGISK" —— 最可靠的家族证据。</summary>
    public string SuVersion { get; init; } = "";

    /// <summary>.allowlist 头 3 字节，"USK" 说明是 KernelSU 格式且可写。</summary>
    public string AllowlistMagic { get; init; } = "";

    public bool HasKsuDir { get; init; }
    public string KsudPath { get; init; } = "";
    public string KsudVersion { get; init; } = "";
    public string KsudInfoVersion { get; init; } = "";

    public bool HasMagiskDb { get; init; }
    public string MagiskPath { get; init; } = "";
    public string MagiskVersion { get; init; } = "";
    public string MagiskVersionCode { get; init; } = "";

    public bool HasApDir { get; init; }
    public string ApdPath { get; init; } = "";
    public string ApatchVersion { get; init; } = "";

    public bool HasModulesDir { get; init; }

    /// <summary>/proc/modules 里的内核模块名（kernelsu / apatch / magisk …），逗号分隔；空=内置。</summary>
    public string KernelModules { get; init; } = "";

    public bool HasSuperSuFiles { get; init; }
    public bool HasPhhFiles { get; init; }
    public bool HasLineageSu { get; init; }

    /// <summary>pm list packages 的原始输出。</summary>
    public string PackageList { get; init; } = "";

    public bool SuOk => SuPath.Length > 0;

    public bool IsKsuAllowlist => AllowlistMagic == "USK";

    public HashSet<string> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasPackage(string package) => Packages.Contains(package);

    public bool LooksKsu =>
        IsKsuAllowlist ||
        HasKsuDir ||
        KsudPath.Length > 0 ||
        KernelModules.Contains("ksu", StringComparison.OrdinalIgnoreCase) ||
        KernelModules.Contains("kernelsu", StringComparison.OrdinalIgnoreCase) ||
        SuVersion.Contains("kernelsu", StringComparison.OrdinalIgnoreCase) ||
        SuVersion.Contains("sukisu", StringComparison.OrdinalIgnoreCase) ||
        SuVersion.Contains("resukisu", StringComparison.OrdinalIgnoreCase) ||
        SuVersion.Contains("kowsu", StringComparison.OrdinalIgnoreCase) ||
        SuVersion.Contains("ksunext", StringComparison.OrdinalIgnoreCase) ||
        SuVersion.Contains(":ksu", StringComparison.OrdinalIgnoreCase);

    public bool LooksMagisk =>
        HasMagiskDb ||
        MagiskPath.Length > 0 ||
        SuVersion.Contains("magisk", StringComparison.OrdinalIgnoreCase);

    public bool LooksAPatch =>
        HasApDir ||
        ApdPath.Length > 0 ||
        SuVersion.Contains("apatch", StringComparison.OrdinalIgnoreCase);

    public bool LooksSuperSu =>
        HasSuperSuFiles ||
        HasPackage("eu.chainfire.supersu") ||
        SuVersion.Contains("supersu", StringComparison.OrdinalIgnoreCase);

    public bool LooksPhh =>
        HasPhhFiles ||
        HasPackage("me.phh.superuser") ||
        SuVersion.Contains("phh", StringComparison.OrdinalIgnoreCase) ||
        SuVersion.Contains("superuser", StringComparison.OrdinalIgnoreCase);

    public bool LooksLineageSu =>
        HasLineageSu ||
        HasPackage(RootPackages.LineageSettings) &&
        SuVersion.Contains("lineage", StringComparison.OrdinalIgnoreCase);

    /// <summary>从 "3.3.1:KernelSU" 里取出冒号后面的实现名。</summary>
    public string SuVariant
    {
        get
        {
            var i = SuVersion.IndexOf(':');
            return i >= 0 && i + 1 < SuVersion.Length ? SuVersion[(i + 1)..].Trim() : "";
        }
    }

    /// <summary>从 su -v 里取出冒号前面的版本号。</summary>
    public string SuVersionNumber
    {
        get
        {
            var i = SuVersion.IndexOf(':');
            return i > 0 ? SuVersion[..i].Trim() : "";
        }
    }

    /// <summary>把 /proc/modules 里的模块名做整体小写匹配。</summary>
    public bool KernelModuleContains(string name)
        => KernelModules.Contains(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>用 pm list packages 的输出填充包集合（调用方只需调用一次）。</summary>
    public void FillPackages()
    {
        if (Packages.Count > 0) return;
        foreach (var raw in PackageList.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("package:", StringComparison.Ordinal)) continue;
            var pkg = line[8..].Trim();
            if (pkg.Length > 0) Packages.Add(pkg);
        }
    }

    public string FirstPackage(params string[] candidates)
        => candidates.FirstOrDefault(HasPackage) ?? "";
}
