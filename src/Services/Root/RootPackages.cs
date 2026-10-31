using System;
using System.Collections.Generic;

namespace DashFile.Services.Root;

/// <summary>
/// 已知 Root 管理器包名。识别仍以设备证据为主，这里只用于展示名、版本和
/// “是否装了管理器”；集中常量是为了避免各 provider 里散落字符串。
/// </summary>
public static class RootPackages
{
    // KernelSU 系
    public const string KernelSu = "me.weishu.kernelsu";
    public const string KernelSuNext = "com.rifsxd.ksunext";
    public const string SukiSuUltra = "com.sukisu.ultra";
    public const string ReSukiSU = "com.resukisu.resukisu";
    public const string KowSU = "com.kowstudio.kowsu";
    public const string MKSU = "com.mohamadamin.mksu";

    // Magisk 系
    public const string Magisk = "com.topjohnwu.magisk";
    public const string MagiskAlpha = "io.github.vvb2060.magisk";
    public const string KitsuneMask = "io.github.huskydg.magisk";

    // APatch / 传统实现
    public const string APatch = "me.bmax.apatch";
    public const string SuperSu = "eu.chainfire.supersu";
    public const string PhhSuperuser = "me.phh.superuser";
    public const string LineageSettings = "org.lineageos.settings";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        KernelSu, KernelSuNext, SukiSuUltra, ReSukiSU, KowSU, MKSU,
        Magisk, MagiskAlpha, KitsuneMask,
        APatch, SuperSu, PhhSuperuser,
    };

    public static string ManagerDataDir(string package) => DevicePaths.DataRoot + "/" + package;
}
