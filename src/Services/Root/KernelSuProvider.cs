using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DashFile.Services.Root;

/// <summary>
/// KernelSU 系（KernelSU / KernelSU-Next / SukiSU Ultra / ReSukiSU / KowSU / MKSU / 各种 fork）。
/// 授权表是二进制 /data/adb/ksu/.allowlist，已有 SuperuserService 负责编解码；
/// 模块安装走 ksud，没有 ksud 时由 ModuleService 退回"直接部署"。
/// </summary>
public sealed class KernelSuProvider : IRootProvider
{
    private readonly RemoteFileSystem _fs;
    private readonly SuperuserService _su;
    private readonly string _ksud;
    private readonly string _ksudCmd;
    private readonly bool _allowlistOk;
    private bool _formatUnsupported;

    public KernelSuProvider(RemoteFileSystem fs, string displayName, string version, string workMode,
        bool allowlistOk, string managerPackage, bool managerInstalled, string ksudCommand = "")
    {
        _fs = fs;
        // 传 fork 名进去：新版授权表（784 步长）只有 1 条记录时与老版同长同偏移，只能靠它区分
        _su = new SuperuserService(fs, displayName);
        DisplayName = displayName;
        Version = string.IsNullOrWhiteSpace(version) ? "无管理器" : version;
        WorkMode = workMode;
        _allowlistOk = allowlistOk;
        ManagerPackage = managerPackage;
        ManagerInstalled = managerInstalled;
        _ksud = string.IsNullOrWhiteSpace(ksudCommand) ? "ksud" : ksudCommand;
        _ksudCmd = _ksud.Contains(' ') ? Sh.Q(_ksud) : _ksud;
    }

    public string Id => "ksu";
    public RootFamily Family => RootFamily.KernelSu;
    public string DisplayName { get; }
    public string Version { get; }
    public string WorkMode { get; }
    public string ManagerPackage { get; }
    public bool ManagerInstalled { get; }

    // 名字叫 KernelSU 不代表授权表就能写：fork 改过格式、或者根本没装管理器时都可能不可写。
    //   只有"文件在、密数对、但布局不认识"才永久降级为只读；临时读失败（adb 抖动）下次重试即可。
    public bool CanReadGrants => _allowlistOk && !_formatUnsupported;
    public bool CanManageGrants => _allowlistOk && !_formatUnsupported;
    public string GrantHint => CanManageGrants
        ? _su.LayoutAmbiguous
            ? "直接读写 /data/adb/ksu/.allowlist；当前表只有 1 条记录，步长无法判定 —— 可以改已有授权，新增请先在管理器里多授权一个应用"
            : "直接读写 /data/adb/ksu/.allowlist（二进制授权表）"
        : _formatUnsupported
            ? "检测到 KernelSU 系，但授权表不是可识别的 USK 格式，已降级为只读"
            : "授权表读取失败，请刷新或重新连接设备";
    public bool GrantChangeNeedsReboot => true;

    public string? ModuleInstallCommand => _ksudCmd + " module install {0}";
    public string? ModuleUninstallCommand => _ksudCmd + " module uninstall {0}";

    public async Task<List<RootGrant>> ReadGrantsAsync(CancellationToken ct = default)
    {
        var result = new List<RootGrant>();
        var table = await _su.ReadAsync(ct).ConfigureAwait(false);
        if (table == null)
        {
            // 只有布局不认识才降级；临时失败不锁死，下次还能读
            _formatUnsupported = _su.FormatUnsupported;
            if (!string.IsNullOrEmpty(_su.LastError)) AppLog.Add("授权表读取失败：" + _su.LastError);
            return result;
        }
        _formatUnsupported = false;
        foreach (var e in table) result.Add(new RootGrant(e.Package, e.Uid, e.Allowed));
        return result;
    }

    public async Task<RootActionResult> SetGrantAsync(string package, int uid, bool allowed,
        CancellationToken ct = default)
    {
        var (ok, msg) = await _su.SetAllowedAsync(package, uid, allowed, ct).ConfigureAwait(false);
        return new RootActionResult(ok, msg);
    }

    public async Task<RootActionResult> RemoveGrantAsync(string package, CancellationToken ct = default)
    {
        var (ok, msg) = await _su.RemoveAsync(package, ct).ConfigureAwait(false);
        return new RootActionResult(ok, msg);
    }

    public async Task<RootActionResult> SoftRebootAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _fs.ExecuteAsync(_ksudCmd + " soft-reboot", true, ct).ConfigureAwait(false);
            if (r.Ok) return new RootActionResult(true, "已发送软重启命令（约 30 秒）");

            // 部分 fork 没把 ksud 放进 PATH，也没有 soft-reboot 子命令：退回 zygote 重启
            var fallback = await _fs.ExecuteAsync("setprop ctl.restart zygote", true, ct).ConfigureAwait(false);
            return fallback.Ok
                ? new RootActionResult(true, "ksud 不可用，已改为重启 zygote（约 10~20 秒）")
                : new RootActionResult(false, "软重启失败，请手动重启设备");
        }
        catch (Exception ex)
        {
            return new RootActionResult(false, "软重启失败：" + ex.Message);
        }
    }
}
