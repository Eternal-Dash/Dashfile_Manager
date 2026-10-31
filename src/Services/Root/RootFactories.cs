using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DashFile.Services.Root;

/// <summary>
/// 内置 Root 识别工厂的注册表。顺序就是优先级：
/// KSU 系 → APatch → Magisk 系 → SuperSU → phh → Lineage → Unknown。
///
/// 新增一种管理器时：
///   1. 实现 IRootProvider + 一个新的 IRootProviderFactory；
///   2. 在 Register 里插入（或外部调用 Register）；
///   3. 不需要改 DetectAsync 的 if/else。
/// </summary>
public static class RootProviderRegistry
{
    private static readonly object Gate = new();
    private static readonly List<IRootProviderFactory> Builtins = new()
    {
        new KernelSuRootFactory(),
        new APatchRootFactory(),
        new MagiskRootFactory(),
        new SuperSuRootFactory(),
        new PhhRootFactory(),
        new LineageRootFactory(),
        new UnknownRootFactory(),
    };

    public static IReadOnlyList<IRootProviderFactory> Factories
    {
        get { lock (Gate) return Builtins.ToArray(); }
    }

    /// <summary>注册到 Unknown 之前（保证自定义实现先被尝试）。</summary>
    public static void Register(IRootProviderFactory factory)
    {
        if (factory == null) return;
        lock (Gate)
        {
            var index = Math.Max(0, Builtins.Count - 1);
            Builtins.Insert(index, factory);
        }
    }
}

internal sealed class KernelSuRootFactory : IRootProviderFactory
{
    public async Task<IRootProvider?> TryCreateAsync(RootDetectionContext context)
    {
        var evidence = context.Evidence;
        if (!evidence.LooksKsu) return null;

        var variant = RootVariants.PickKsu(evidence);
        var managerPackage = variant.Package;
        var managerInstalled = managerPackage.Length > 0;
        var managerVersion = await context.PackageVersionAsync(managerPackage).ConfigureAwait(false);

        var version = RootVariants.FormatVersion(
            RootVariants.FirstVersion(managerVersion, evidence.KsudInfoVersion,
                evidence.KsudVersion, evidence.SuVersionNumber),
            managerInstalled);

        var workMode = evidence.KernelModuleContains("kernelsu") || evidence.KernelModuleContains("ksu")
            ? "LKM（模块加载）"
            : "Built-in（内核内置）";

        // 能不能写授权表由指纹决定，而不是由名字决定（fork 可能改格式）
        return new KernelSuProvider(context.FileSystem, variant.Name, version, workMode,
            evidence.IsKsuAllowlist, managerPackage, managerInstalled, evidence.KsudPath);
    }
}

internal sealed class APatchRootFactory : IRootProviderFactory
{
    public async Task<IRootProvider?> TryCreateAsync(RootDetectionContext context)
    {
        var evidence = context.Evidence;
        if (!evidence.LooksAPatch) return null;

        var managerPackage = evidence.FirstPackage(RootPackages.APatch);
        var managerInstalled = managerPackage.Length > 0;
        var version = RootVariants.FormatVersion(
            RootVariants.FirstVersion(await context.PackageVersionAsync(managerPackage).ConfigureAwait(false),
                evidence.ApatchVersion, evidence.SuVersionNumber),
            managerInstalled);

        return new APatchProvider(context.FileSystem,
            managerInstalled ? "APatch" : "APatch（无管理器）",
            version,
            "内核补丁（KernelPatch）",
            evidence.ApdPath,
            managerPackage,
            managerInstalled);
    }
}

internal sealed class MagiskRootFactory : IRootProviderFactory
{
    public async Task<IRootProvider?> TryCreateAsync(RootDetectionContext context)
    {
        var evidence = context.Evidence;
        if (!evidence.LooksMagisk) return null;

        var variant = RootVariants.PickMagisk(evidence, out var hasManagerPackage);
        var managerPackage = hasManagerPackage ? variant.Package : "";
        var managerInstalled = managerPackage.Length > 0;
        var version = RootVariants.FormatVersion(
            RootVariants.FirstVersion(
                await context.PackageVersionAsync(managerPackage).ConfigureAwait(false),
                RootVariants.StripColon(evidence.MagiskVersion),
                evidence.SuVersionNumber),
            managerInstalled);

        return new MagiskProvider(context.FileSystem, variant.Name, version, "用户态（MagiskSU）",
            string.IsNullOrWhiteSpace(evidence.MagiskPath) ? "magisk" : evidence.MagiskPath,
            evidence.HasMagiskDb,
            managerPackage,
            managerInstalled);
    }
}

internal sealed class SuperSuRootFactory : IRootProviderFactory
{
    public async Task<IRootProvider?> TryCreateAsync(RootDetectionContext context)
    {
        var evidence = context.Evidence;
        if (!evidence.LooksSuperSu) return null;

        var managerPackage = evidence.FirstPackage(RootPackages.SuperSu);
        var managerInstalled = managerPackage.Length > 0;
        var version = RootVariants.FormatVersion(
            RootVariants.FirstVersion(
                await context.PackageVersionAsync(managerPackage).ConfigureAwait(false),
                evidence.SuVersionNumber),
            managerInstalled);

        return new SuperSuProvider(context.FileSystem, version, "传统 su（System）",
            managerPackage, managerInstalled);
    }
}

internal sealed class PhhRootFactory : IRootProviderFactory
{
    public async Task<IRootProvider?> TryCreateAsync(RootDetectionContext context)
    {
        var evidence = context.Evidence;
        if (!evidence.LooksPhh) return null;

        var managerPackage = evidence.FirstPackage(RootPackages.PhhSuperuser);
        var managerInstalled = managerPackage.Length > 0;
        var version = RootVariants.FormatVersion(
            RootVariants.FirstVersion(
                await context.PackageVersionAsync(managerPackage).ConfigureAwait(false),
                evidence.SuVersionNumber),
            managerInstalled);

        return new PhhSuperuserProvider(context.FileSystem, version, "传统 su（phh）",
            managerPackage, managerInstalled);
    }
}

internal sealed class LineageRootFactory : IRootProviderFactory
{
    public Task<IRootProvider?> TryCreateAsync(RootDetectionContext context)
    {
        var evidence = context.Evidence;
        if (!evidence.LooksLineageSu) return Task.FromResult<IRootProvider?>(null);

        var version = RootVariants.FormatVersion(
            RootVariants.FirstVersion(evidence.SuVersionNumber), false);
        return Task.FromResult<IRootProvider?>(
            new LineageSuProvider(context.FileSystem, version, "系统内置 su"));
    }
}

internal sealed class UnknownRootFactory : IRootProviderFactory
{
    public Task<IRootProvider?> TryCreateAsync(RootDetectionContext context)
    {
        var evidence = context.Evidence;
        if (!evidence.SuOk) return Task.FromResult<IRootProvider?>(null);

        var version = RootVariants.FormatVersion(
            RootVariants.FirstVersion(evidence.SuVersionNumber), false);
        return Task.FromResult<IRootProvider?>(new UnknownRootProvider(version));
    }
}
