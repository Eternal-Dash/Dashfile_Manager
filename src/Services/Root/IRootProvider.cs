using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DashFile.Services.Root;

/// <summary>root 实现家族。UI 和模块逻辑按家族做能力判断，避免到处判字符串。</summary>
public enum RootFamily
{
    Unknown = 0,
    KernelSu = 1,
    Magisk = 2,
    APatch = 3,
    SuperSu = 4,
    PhhSuperuser = 5,
    LineageSu = 6,
}

/// <summary>一条 root 授权记录。</summary>
public sealed record RootGrant(string Package, int Uid, bool Allowed);

/// <summary>root 操作结果（带给用户看的说明）。</summary>
public sealed record RootActionResult(bool Ok, string Message);

/// <summary>
/// 各种 root 实现的统一接口。
///
/// 为什么要这一层：KernelSU 系（KSU / KSU-Next / SukiSU / ReSukiSU / KowSU）、
/// Magisk 系（Magisk / Alpha / Kitsune）、APatch、SuperSU、phh、LineageOS SU
/// 的「模块机制」基本一样（/data/adb/modules + disable/remove 标记），
/// 但「授权表」「模块安装命令」「软重启方式」完全不同。
///
/// 新增一个实现 = 新增一个类 + 在 RootProviders 里加识别分支，UI 不需要改。
/// </summary>
public interface IRootProvider
{
    /// <summary>内部标识：ksu / magisk / apatch / supersu / phh / lineage / unknown</summary>
    string Id { get; }

    /// <summary>所属家族，用于统一的 UI 文案和插件 API。</summary>
    RootFamily Family { get; }

    /// <summary>展示名：KernelSU / KernelSU-Next / SukiSU Ultra / Magisk / Magisk Alpha …</summary>
    string DisplayName { get; }

    /// <summary>版本（拿不到时给 "无管理器" / "—"）</summary>
    string Version { get; }

    /// <summary>工作模式：LKM / Built-in / 用户态 / 内核补丁 …</summary>
    string WorkMode { get; }

    /// <summary>管理器 App 包名；没有安装管理器时为空。</summary>
    string ManagerPackage { get; }

    /// <summary>是否检测到管理器 App（不装管理器也可能有 root）。</summary>
    bool ManagerInstalled { get; }

    /// <summary>能否读取授权表。</summary>
    bool CanReadGrants { get; }

    /// <summary>能否直接改写授权表（false 时 UI 只展示，不改写）。</summary>
    bool CanManageGrants { get; }

    /// <summary>授权表存储方式 / 不可写原因，展示给用户。</summary>
    string GrantHint { get; }

    /// <summary>改完授权后是否必须重启（KSU 需要，MagiskSU 不需要）。</summary>
    bool GrantChangeNeedsReboot { get; }

    /// <summary>
    /// 模块安装命令模板（{0} = 设备上的 zip 路径）；null 表示用通用的
    /// "解压到 /data/adb/modules_update + touch .update" 直接部署方式。
    /// </summary>
    string? ModuleInstallCommand { get; }

    /// <summary>模块卸载命令模板（{0} = 模块 id）；null 表示打 remove 标记。</summary>
    string? ModuleUninstallCommand { get; }

    /// <summary>读取全部授权（不支持时返回空表，由 UI 提示）。</summary>
    Task<List<RootGrant>> ReadGrantsAsync(CancellationToken ct = default);

    /// <summary>设置某个包的授权。</summary>
    Task<RootActionResult> SetGrantAsync(string package, int uid, bool allowed,
        CancellationToken ct = default);

    /// <summary>从授权表移除某个包。</summary>
    Task<RootActionResult> RemoveGrantAsync(string package, CancellationToken ct = default);

    /// <summary>软重启（重启 Android 用户态，不丢数据）。</summary>
    Task<RootActionResult> SoftRebootAsync(CancellationToken ct = default);
}
