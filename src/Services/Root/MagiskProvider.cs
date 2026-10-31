using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DashFile.Services.Root;

/// <summary>
/// Magisk 系（Magisk / Magisk Alpha / Kitsune Mask（原 Magisk Delta））。
///
/// 模块机制与 KernelSU 完全一致（/data/adb/modules + disable/remove 标记），
/// 安装用 `magisk --install-module`；授权表是 magisk.db（SQLite），
/// 通过 Magisk 自带的 `magisk --sqlite` 读写，不依赖电脑端 SQLite 库。
///
/// 写入策略：
///  1. 先把 magisk.db 备份到电脑（%LOCALAPPDATA%\DashFile\su\）；
///  2. 先查 policies 是否已有该 uid，有则 UPDATE，没有则 INSERT OR REPLACE；
///  3. 立刻回读校验；不一致就报告失败并给出备份路径，绝不假装成功。
/// MagiskSU 每次授权都会查库，所以改完立即生效，不需要重启。
/// </summary>
public sealed class MagiskProvider : IRootProvider
{
    private readonly RemoteFileSystem _fs;
    private readonly string _magisk;
    private readonly string _magiskCmd;
    private readonly bool _canManage;
    private readonly string _dbPath = DevicePaths.MagiskDb;

    public MagiskProvider(RemoteFileSystem fs, string displayName, string version, string workMode,
        string magiskCommand, bool hasMagiskDb, string managerPackage, bool managerInstalled)
    {
        _fs = fs;
        _magisk = string.IsNullOrWhiteSpace(magiskCommand) ? "magisk" : magiskCommand;
        _magiskCmd = _magisk.Contains(' ') ? Sh.Q(_magisk) : _magisk;
        _canManage = hasMagiskDb && !string.IsNullOrWhiteSpace(magiskCommand);
        DisplayName = displayName;
        Version = string.IsNullOrWhiteSpace(version) ? "无管理器" : version;
        WorkMode = workMode;
        ManagerPackage = managerPackage;
        ManagerInstalled = managerInstalled;
    }

    public string Id => "magisk";
    public RootFamily Family => RootFamily.Magisk;
    public string DisplayName { get; }
    public string Version { get; }
    public string WorkMode { get; }
    public string ManagerPackage { get; }
    public bool ManagerInstalled { get; }

    /// <summary>最近一次读取失败原因（成功时为 null）：别再让"读失败"看起来像"没有授权"</summary>
    public string? LastError { get; private set; }

    public bool CanReadGrants => _canManage;
    public bool CanManageGrants => _canManage;
    public string GrantHint => _canManage
        ? "读写 magisk.db（通过 magisk --sqlite），改动立即生效"
        : "magisk 二进制或 magisk.db 不可用，授权表只读";
    public bool GrantChangeNeedsReboot => false;

    public string? ModuleInstallCommand => _magiskCmd + " --install-module {0}";
    public string? ModuleUninstallCommand => null;

    public async Task<List<RootGrant>> ReadGrantsAsync(CancellationToken ct = default)
    {
        var result = new List<RootGrant>();
        if (!_canManage) return result;

        try
        {
            // ① 包名 → uid（一次调用拿全，避免逐个 stat）
            var pm = await _fs.RunCommandAsync("pm list packages -U 2>/dev/null", false, ct).ConfigureAwait(false);
            var uidOf = new Dictionary<int, string>();
            foreach (var raw in pm.Split('\n'))
            {
                var t = raw.Trim();
                if (!t.StartsWith("package:", StringComparison.Ordinal)) continue;
                var body = t[8..];
                var at = body.LastIndexOf(" uid:", StringComparison.Ordinal);
                if (at < 0) continue;
                if (!int.TryParse(body[(at + 5)..].Trim(), out var uid)) continue;
                var pkg = body[..at].Trim();
                if (pkg.Length > 0) uidOf[uid] = pkg;
            }

            // ② policies 表
            var rawPol = await SqliteAsync("SELECT uid, policy FROM policies", ct).ConfigureAwait(false);
            foreach (var raw in rawPol.Split('\n'))
            {
                var t = raw.Trim();
                if (t.Length == 0 || t.StartsWith("[")) continue;
                var parts = t.Split(new[] { '|', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (!int.TryParse(parts[0].Trim(), out var uid)) continue;
                if (!int.TryParse(parts[1].Trim(), out var pol)) continue;
                if (!uidOf.TryGetValue(uid, out var pkg)) continue;
                result.Add(new RootGrant(pkg, uid, pol == 1));
            }
        }
        catch (Exception ex)
        {
            // 以前整段 catch{}：读失败也会显示"0 条授权"，用户以为授权丢了/去改表更危险
            LastError = "读取 magisk.db 失败：" + ex.Message;
            AppLog.Add("[magisk] 读取授权失败：" + ex.Message);
        }
        return result;
    }

    public async Task<RootActionResult> SetGrantAsync(string package, int uid, bool allowed,
        CancellationToken ct = default)
    {
        if (!_canManage) return new RootActionResult(false, "Magisk 授权表不可写：" + GrantHint);
        if (uid <= 0) return new RootActionResult(false, "UID 无效，未做任何修改");

        var backup = await BackupDbAsync(ct).ConfigureAwait(false);
        var value = allowed ? 1 : 0;

        try
        {
            var existingRaw = await SqliteAsync($"SELECT policy FROM policies WHERE uid={uid}", ct)
                .ConfigureAwait(false);
            var hasRow = Regex.IsMatch(existingRaw, @"(?m)^\s*\d+\s*$");

            var sql = !hasRow
                ? $"INSERT OR REPLACE INTO policies (uid, policy, until, logging) VALUES ({uid}, {value}, 0, 1)"
                : $"UPDATE policies SET policy={value} WHERE uid={uid}";

            var r = await ExecuteSqliteAsync(sql, ct).ConfigureAwait(false);
            if (!r.Ok)
            {
                // 老版本 schema 可能没有 until/logging 列，退回两列写法
                if (!hasRow)
                {
                    r = await ExecuteSqliteAsync(
                        $"INSERT OR REPLACE INTO policies (uid, policy) VALUES ({uid}, {value})", ct)
                        .ConfigureAwait(false);
                }
                if (!r.Ok)
                    return new RootActionResult(false, $"写入 magisk.db 失败：{FirstLine(r.All)}" +
                                                      (backup != null ? $"\n备份：{backup}" : ""));
            }

            // 直接回读这一条 policy，避免包名映射（pm -U）在个别 ROM 上缺失导致误判
            var verifyRaw = await SqliteAsync($"SELECT policy FROM policies WHERE uid={uid}", ct)
                .ConfigureAwait(false);
            var vm = Regex.Match(verifyRaw, @"(?m)^\s*(\d+)\s*$");
            var match = vm.Success && int.TryParse(vm.Groups[1].Value, out var got) && got == value;
            return match
                ? new RootActionResult(true, "已写入 magisk.db，立即生效")
                : new RootActionResult(false, "写入后回读不一致，已保留备份" +
                                              (backup != null ? $"：{backup}" : ""));
        }
        catch (Exception ex)
        {
            return new RootActionResult(false, "写入 magisk.db 失败：" + ex.Message +
                                              (backup != null ? $"\n备份：{backup}" : ""));
        }
    }

    public async Task<RootActionResult> RemoveGrantAsync(string package, CancellationToken ct = default)
    {
        if (!_canManage) return new RootActionResult(false, "Magisk 授权表不可写：" + GrantHint);
        try
        {
            var uidRaw = await _fs.RunCommandAsync(
                "stat -c %u " + Sh.Q(DevicePaths.UserData(package)) + " 2>/dev/null", true, ct)
                .ConfigureAwait(false);
            var m = Regex.Match(uidRaw, @"(?m)^\s*(\d+)\s*$");
            if (!m.Success) return new RootActionResult(false, "取不到 UID");
            var uid = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            var backup = await BackupDbAsync(ct).ConfigureAwait(false);
            var r = await ExecuteSqliteAsync($"DELETE FROM policies WHERE uid={uid}", ct).ConfigureAwait(false);
            return r.Ok
                ? new RootActionResult(true, "已从 magisk.db 移除" +
                                              (backup != null ? $"（备份：{backup}）" : ""))
                : new RootActionResult(false, "移除失败：" + FirstLine(r.All));
        }
        catch (Exception ex) { return new RootActionResult(false, "移除失败：" + ex.Message); }
    }

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

    // ---------------- magisk --sqlite ----------------

    private async Task<string> SqliteAsync(string sql, CancellationToken ct)
    {
        try
        {
            var r = await _fs.ExecuteAsync($"{_magiskCmd} --sqlite {Sh.Q(sql)}", true, ct).ConfigureAwait(false);
            return r.All;
        }
        catch { return ""; }
    }

    private async Task<ProcResult> ExecuteSqliteAsync(string sql, CancellationToken ct)
    {
        var r = await _fs.ExecuteAsync($"{_magiskCmd} --sqlite {Sh.Q(sql)}", true, ct).ConfigureAwait(false);
        if (r.ExitCode == 0 && !r.All.Contains("error", StringComparison.OrdinalIgnoreCase) &&
            !r.All.Contains("no such", StringComparison.OrdinalIgnoreCase) &&
            !r.All.Contains("syntax", StringComparison.OrdinalIgnoreCase))
            return r;
        return new ProcResult(r.ExitCode == 0 ? -2 : r.ExitCode, r.StdOut, r.StdErr);
    }

    private async Task<string?> BackupDbAsync(CancellationToken ct)
    {
        try
        {
            var dir = AppPaths.Su;
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "magisk-" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".db");
            var bytes = await _fs.ReadBytesAsync(_dbPath, ct).ConfigureAwait(false);
            if (bytes.Length == 0) return null;
            await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
            return file;
        }
        catch { return null; }
    }

    private static string FirstLine(string s)
    {
        foreach (var line in (s ?? "").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0 && !t.StartsWith("[")) return t;
        }
        return "未知错误";
    }
}
