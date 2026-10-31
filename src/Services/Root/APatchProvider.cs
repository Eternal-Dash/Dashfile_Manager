using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DashFile.Services.Root;

/// <summary>
/// APatch（基于 KernelPatch 的内核级 root）。
/// 模块机制沿用 KernelSU 那套（/data/adb/modules + modules_update），
/// 安装优先尝试 `apd module install`，失败后 ModuleService 会自动退回“直接部署”；
/// 它的授权存储（/data/adb/ap 下的 package_config）没有稳定公开的写入协议，
/// 因此授权表保持只读，避免写坏用户的 root 授权。
/// </summary>
public sealed class APatchProvider : IRootProvider
{
    private readonly RemoteFileSystem _fs;
    private readonly string _apd;
    private readonly string _apdCmd;

    public APatchProvider(RemoteFileSystem fs, string displayName, string version, string workMode,
        string apdCommand, string managerPackage, bool managerInstalled)
    {
        _fs = fs;
        _apd = string.IsNullOrWhiteSpace(apdCommand) ? "" : apdCommand;
        _apdCmd = _apd.Contains(' ') ? Sh.Q(_apd) : _apd;
        DisplayName = displayName;
        Version = string.IsNullOrWhiteSpace(version) ? "无管理器" : version;
        WorkMode = workMode;
        ManagerPackage = managerPackage;
        ManagerInstalled = managerInstalled;
    }

    public string Id => "apatch";
    public RootFamily Family => RootFamily.APatch;
    public string DisplayName { get; }
    public string Version { get; }
    public string WorkMode { get; }
    public string ManagerPackage { get; }
    public bool ManagerInstalled { get; }

    public bool CanReadGrants => false;
    public bool CanManageGrants => false;
    public string GrantHint => "APatch 的授权存储由 apd 管理，暂不支持直接读写，请在 APatch App 里修改";
    public bool GrantChangeNeedsReboot => true;

    // APatch 的 apd 确实提供 module 子命令；没确认时 ModuleService 会自动退回通用部署。
    public string? ModuleInstallCommand => _apdCmd.Length == 0 ? null : _apdCmd + " module install {0}";
    public string? ModuleUninstallCommand => _apdCmd.Length == 0 ? null : _apdCmd + " module uninstall {0}";

    public Task<List<RootGrant>> ReadGrantsAsync(CancellationToken ct = default)
        => Task.FromResult(new List<RootGrant>());

    public Task<RootActionResult> SetGrantAsync(string package, int uid, bool allowed,
        CancellationToken ct = default)
        => Task.FromResult(new RootActionResult(false, GrantHint));

    public Task<RootActionResult> RemoveGrantAsync(string package, CancellationToken ct = default)
        => Task.FromResult(new RootActionResult(false, GrantHint));

    public async Task<RootActionResult> SoftRebootAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _fs.ExecuteAsync("setprop ctl.restart zygote", true, ct).ConfigureAwait(false);
            return r.Ok
                ? new RootActionResult(true, "已重启 zygote（约 10~20 秒，不会丢数据）")
                : new RootActionResult(false, "软重启失败，请手动重启设备");
        }
        catch (Exception ex) { return new RootActionResult(false, "软重启失败：" + ex.Message); }
    }
}

/// <summary>检测到有 root，但实现未知：全部按"只读"处理，不做任何写入。</summary>
public sealed class UnknownRootProvider : IRootProvider
{
    public UnknownRootProvider(string version = "")
    {
        Version = string.IsNullOrWhiteSpace(version) ? "—" : version;
    }

    public string Id => "unknown";
    public RootFamily Family => RootFamily.Unknown;
    public string DisplayName => "未知实现";
    public string Version { get; }
    public string WorkMode => "—";
    public string ManagerPackage => "";
    public bool ManagerInstalled => false;
    public bool CanReadGrants => false;
    public bool CanManageGrants => false;
    public string GrantHint => "未知的 Root 实现，未做任何修改";
    public bool GrantChangeNeedsReboot => true;
    public string? ModuleInstallCommand => null;
    public string? ModuleUninstallCommand => null;

    public Task<List<RootGrant>> ReadGrantsAsync(CancellationToken ct = default)
        => Task.FromResult(new List<RootGrant>());

    public Task<RootActionResult> SetGrantAsync(string package, int uid, bool allowed,
        CancellationToken ct = default)
        => Task.FromResult(new RootActionResult(false, "未知的 Root 实现，未做任何修改"));

    public Task<RootActionResult> RemoveGrantAsync(string package, CancellationToken ct = default)
        => Task.FromResult(new RootActionResult(false, "未知的 Root 实现，未做任何修改"));

    public Task<RootActionResult> SoftRebootAsync(CancellationToken ct = default)
        => Task.FromResult(new RootActionResult(false, "未知的 Root 实现，未执行软重启"));
}
