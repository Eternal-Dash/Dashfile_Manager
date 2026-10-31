using System;
using System.Linq;

namespace DashFile.Services.Root;

internal sealed record RootVariant(string Name, string Package);

/// <summary>
/// 显示名、包名、版本号的整理逻辑。识别顺序仍在各 factory 里，这里只处理“认出来之后叫什么”。
/// </summary>
internal static class RootVariants
{
    // 顺序 = 优先级。同一个 KSU 内核可能同时存在多个管理器 App（例如 KSU + SukiSU 共存），
    // 取“更具体”的那个展示，避免把 SukiSU 显示成 KernelSU。
    private static readonly RootVariant[] KsuManagers =
    {
        new("SukiSU Ultra", RootPackages.SukiSuUltra),
        new("ReSukiSU", RootPackages.ReSukiSU),
        new("KernelSU-Next", RootPackages.KernelSuNext),
        new("KowSU", RootPackages.KowSU),
        new("MKSU", RootPackages.MKSU),
        new("KernelSU", RootPackages.KernelSu),
    };

    private static readonly RootVariant[] MagiskManagers =
    {
        new("Magisk Alpha", RootPackages.MagiskAlpha),
        new("Kitsune Mask", RootPackages.KitsuneMask),
        new("Magisk", RootPackages.Magisk),
    };

    public static RootVariant PickKsu(RootEvidence evidence)
    {
        foreach (var variant in KsuManagers)
            if (evidence.HasPackage(variant.Package)) return variant;

        var fork = evidence.SuVariant;
        var name = fork switch
        {
            _ when fork.Contains("SukiSU", StringComparison.OrdinalIgnoreCase) => "SukiSU Ultra",
            _ when fork.Contains("ReSukiSU", StringComparison.OrdinalIgnoreCase) => "ReSukiSU",
            _ when fork.Contains("KowSU", StringComparison.OrdinalIgnoreCase) => "KowSU",
            _ when fork.Contains("KSUN", StringComparison.OrdinalIgnoreCase) ||
                   fork.Contains("KSU-Next", StringComparison.OrdinalIgnoreCase) => "KernelSU-Next",
            _ when fork.Contains("MKSU", StringComparison.OrdinalIgnoreCase) => "MKSU",
            _ when fork.Contains("KernelSU", StringComparison.OrdinalIgnoreCase) => "KernelSU",
            _ => "KernelSU",
        };
        return new RootVariant(name + "（无管理器）", "");
    }

    public static RootVariant PickMagisk(RootEvidence evidence, out bool hasManagerPackage)
    {
        foreach (var variant in MagiskManagers)
        {
            if (!evidence.HasPackage(variant.Package)) continue;
            hasManagerPackage = true;
            return variant;
        }

        hasManagerPackage = false;
        var name = evidence.SuVariant.Contains("alpha", StringComparison.OrdinalIgnoreCase) ? "Magisk Alpha"
                 : evidence.SuVariant.Contains("kitsune", StringComparison.OrdinalIgnoreCase) ? "Kitsune Mask"
                 : "Magisk";
        return new RootVariant(name + "（无管理器）", "");
    }

    public static string FirstVersion(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    public static string StripColon(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var index = value.IndexOf(':');
        return (index > 0 ? value[..index] : value).Trim();
    }

    public static string FormatVersion(string raw, bool managerInstalled)
    {
        var version = StripColon(raw);
        if (version.Length == 0)
            return managerInstalled ? "—" : "无管理器";
        if (!version.StartsWith("v", StringComparison.OrdinalIgnoreCase)) version = "v" + version;
        return managerInstalled ? version : version + "（无管理器）";
    }
}
