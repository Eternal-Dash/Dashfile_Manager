using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DashFile.Services.Root;

/// <summary>
/// SuperSU（Chainfire）。老设备上仍常见，但授权库是私有 SQLite，且没有稳定的 CLI；
/// 这里只做识别和展示，不读写授权表。模块机制也不适用（SuperSU 不管理 /data/adb/modules）。
/// </summary>
public sealed class SuperSuProvider : IRootProvider
{
    private readonly RemoteFileSystem _fs;

    public SuperSuProvider(RemoteFileSystem fs, string version, string workMode,
        string managerPackage, bool managerInstalled)
    {
        _fs = fs;
        Version = string.IsNullOrWhiteSpace(version) ? "无管理器" : version;
        WorkMode = workMode;
        ManagerPackage = managerPackage;
        ManagerInstalled = managerInstalled;
    }

    public string Id => "supersu";
    public RootFamily Family => RootFamily.SuperSu;
    public string DisplayName => ManagerInstalled ? "SuperSU" : "SuperSU（无管理器）";
    public string Version { get; }
    public string WorkMode { get; }
    public string ManagerPackage { get; }
    public bool ManagerInstalled { get; }
    public bool CanReadGrants => false;
    public bool CanManageGrants => false;
    public string GrantHint => "SuperSU 的授权库是私有格式，暂不支持直接读写，请在 SuperSU App 里修改";
    public bool GrantChangeNeedsReboot => true;
    public string? ModuleInstallCommand => null;
    public string? ModuleUninstallCommand => null;

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

/// <summary>
/// phh's Superuser（AOSP/类原生 ROM 常见的独立 su 管理器）。
/// 只识别和展示；授权库不公开，不直接改写。
/// </summary>
public sealed class PhhSuperuserProvider : IRootProvider
{
    private readonly RemoteFileSystem _fs;

    public PhhSuperuserProvider(RemoteFileSystem fs, string version, string workMode,
        string managerPackage, bool managerInstalled)
    {
        _fs = fs;
        Version = string.IsNullOrWhiteSpace(version) ? "无管理器" : version;
        WorkMode = workMode;
        ManagerPackage = managerPackage;
        ManagerInstalled = managerInstalled;
    }

    public string Id => "phh";
    public RootFamily Family => RootFamily.PhhSuperuser;
    public string DisplayName => ManagerInstalled ? "phh's Superuser" : "phh's Superuser（无管理器）";
    public string Version { get; }
    public string WorkMode { get; }
    public string ManagerPackage { get; }
    public bool ManagerInstalled { get; }
    public bool CanReadGrants => false;
    public bool CanManageGrants => false;
    public string GrantHint => "phh's Superuser 的授权库是私有格式，暂不支持直接读写，请在 Superuser App 里修改";
    public bool GrantChangeNeedsReboot => true;
    public string? ModuleInstallCommand => null;
    public string? ModuleUninstallCommand => null;

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

/// <summary>
/// LineageOS / 类原生 ROM 自带的 su。没有管理器 App、没有授权表，权限由系统设置控制。
/// </summary>
public sealed class LineageSuProvider : IRootProvider
{
    private readonly RemoteFileSystem _fs;

    public LineageSuProvider(RemoteFileSystem fs, string version, string workMode)
    {
        _fs = fs;
        Version = string.IsNullOrWhiteSpace(version) ? "—" : version;
        WorkMode = workMode;
    }

    public string Id => "lineage";
    public RootFamily Family => RootFamily.LineageSu;
    public string DisplayName => "LineageOS SU（系统内置）";
    public string Version { get; }
    public string WorkMode { get; }
    public string ManagerPackage => "";
    public bool ManagerInstalled => false;
    public bool CanReadGrants => false;
    public bool CanManageGrants => false;
    public string GrantHint => "系统内置 su 没有授权表，权限由「开发者选项 → Root 访问」控制";
    public bool GrantChangeNeedsReboot => true;
    public string? ModuleInstallCommand => null;
    public string? ModuleUninstallCommand => null;

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
