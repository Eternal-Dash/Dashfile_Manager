using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DashFile.Models;

namespace DashFile.Services;

public sealed class ModuleService
{
    private readonly RemoteFileSystem _fs;
    private readonly AdbRunner _adb;
    private readonly string? _installCmd;     // 例如 "ksud module install {0}" / "magisk --install-module {0}"
    private readonly string? _uninstallCmd;   // 例如 "ksud module uninstall {0}" / "apd module uninstall {0}"

    public ModuleService(RemoteFileSystem fs, AdbRunner adb, string? installCommand = null,
        string? uninstallCommand = null)
    {
        _fs = fs;
        _adb = adb;
        _installCmd = installCommand;
        _uninstallCmd = uninstallCommand;
    }

    private const string Base = DevicePaths.Modules;

    public async Task<List<ModuleInfo>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<ModuleInfo>();

        // 一次 adb 往返拿回全部模块信息。
        //   原来每个模块要 2 次（cat module.prop + ls 目录）：10 个模块就是 20 次进程往返、
        //   每次 80~110ms，模块页要 2 秒才能出来。现在固定 1 次。
        var script =
            $"cd {Base} 2>/dev/null || exit 0; " +
            "for d in */; do " +
            "d=${d%/}; " +
            "echo \"@@$d\"; " +
            "cat \"$d/module.prop\" 2>/dev/null; " +
            "echo \"@@props_end\"; " +
            "ls -1 \"$d\" 2>/dev/null; " +
            "echo \"@@end\"; " +
            "done";

        var raw = await _fs.RunCommandAsync(script, true, ct).ConfigureAwait(false);

        string? id = null;
        var inProps = false;
        var prop = new System.Text.StringBuilder();
        var files = new List<string>();

        void Flush()
        {
            if (id == null) return;
            var map = ParseProp(prop.ToString());
            if (map.Count > 0)          // module.prop 读不到（空/不存在）的目录直接跳过
            {
                var dir = DevicePaths.ModuleDir(id);
                result.Add(new ModuleInfo
                {
                    Id = id,
                    Name = Get(map, "name", id),
                    Version = Get(map, "version", ""),
                    Author = Get(map, "author", ""),
                    Description = Get(map, "description", ""),
                    Dir = dir,
                    Enabled = !files.Contains("disable"),
                    PendingRemove = files.Contains("remove"),
                    HasWebUI = files.Contains("webroot"),
                    HasAction = files.Contains("action.sh"),
                });
            }
            id = null;
            inProps = false;
            prop.Clear();
            files.Clear();
        }

        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("@@props_end", StringComparison.Ordinal)) { inProps = false; continue; }
            if (line.StartsWith("@@end", StringComparison.Ordinal)) { Flush(); continue; }
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                Flush();
                id = line[2..].Trim();
                inProps = true;
                continue;
            }

            if (id == null) continue;
            if (line.StartsWith("[", StringComparison.Ordinal)) continue;   // 退出码那行

            if (inProps) prop.AppendLine(line);
            else files.Add(line);
        }
        Flush();

        // 固定按名称排序：开关模块时不改变位置
        return result.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, string> ParseProp(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var i = line.IndexOf('=');
            if (i <= 0) continue;
            var k = line[..i].Trim();
            var v = line[(i + 1)..].Trim();
            if (k.Length > 0) d[k] = v;
        }
        return d;
    }

    private static string Get(Dictionary<string, string> map, string key, string def)
        => map.TryGetValue(key, out var v) && v.Length > 0 ? v : def;

    public async Task SetEnabledAsync(ModuleInfo m, bool enabled, CancellationToken ct = default)
    {
        var file = Sh.Q(m.Dir + "/disable");
        // 以前完全不看返回值：touch/rm 失败也照样返回，UI 显示"已启用/已禁用"但设备上没变。
        var cmd = (enabled ? $"rm -f {file}" : $"touch {file}") + " && echo DF_OK";
        var output = await _fs.RunCommandAsync(cmd, true, ct).ConfigureAwait(false);
        if (!output.Contains("DF_OK", StringComparison.Ordinal))
            throw new IOException((enabled ? "启用模块失败：" : "禁用模块失败：") + output.Trim());
    }

    public async Task UninstallAsync(ModuleInfo m, CancellationToken ct = default)
    {
        // ① 优先用当前 root 实现提供的卸载命令（ksud / apd …）
        if (!string.IsNullOrWhiteSpace(_uninstallCmd))
        {
            try
            {
                var cmd = _uninstallCmd!.Replace("{0}", Sh.Q(m.Id));
                var r = await _fs.ExecuteAsync(cmd, true, ct).ConfigureAwait(false);
                var text = r.All ?? "";
                if (r.Ok &&
                    !text.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                    !text.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
                    !text.Contains("No such", StringComparison.OrdinalIgnoreCase))
                    return;
            }
            catch { }
        }

        // ② Magisk / 未知实现：打 remove 标记，重启后由管理器移除（最通用、最安全），但失败要报出来
        var mark = await _fs.RunCommandAsync($"touch {Sh.Q(m.Dir + "/remove")} && echo DF_OK", true, ct)
                             .ConfigureAwait(false);
        if (!mark.Contains("DF_OK", StringComparison.Ordinal))
            throw new IOException("标记卸载失败：" + mark.Trim());
    }

    public async Task<string> RunActionAsync(ModuleInfo m, CancellationToken ct = default)
    {
        var cmd = $"cd {Sh.Q(m.Dir)} && sh action.sh 2>&1";
        return await _fs.RunCommandAsync(cmd, true, ct).ConfigureAwait(false);
    }

    /// <summary>把模块 webroot 复制到 /data/local/tmp 并返回该路径（供下载）</summary>
    public async Task<string> ExportWebRootAsync(ModuleInfo m, CancellationToken ct = default)
    {
        var tmp = DevicePaths.TempDirectory("web_" + m.Id.Replace(" ", "_"));
        await _fs.RunCommandAsync($"rm -rf {Sh.Q(tmp)}", true, ct).ConfigureAwait(false);
        await _fs.RunCommandAsync($"cp -r {Sh.Q(m.Dir + "/webroot")} {Sh.Q(tmp)}", true, ct).ConfigureAwait(false);
        await _fs.RunCommandAsync($"chmod -R 777 {Sh.Q(tmp)}", true, ct).ConfigureAwait(false);
        return tmp;
    }

    /// <summary>把本地 zip 推到设备并用 ksud 安装，实时回传日志</summary>
    public async Task<int> InstallAsync(string localZip, Action<string> onLog, CancellationToken ct = default)
    {
        var remote = DevicePaths.TempFile("mod", ".zip");
        onLog("→ 正在上传模块包到设备…");

        var push = await _adb.RunAsync(new[] { "-s", _fs.Serial, "push", localZip, remote }, _adb.TransferTimeout, ct).ConfigureAwait(false);
        onLog(push.All.Trim());
        if (!push.Ok) { onLog("× 上传失败"); return -1; }

        // ① 优先用 provider 提供的安装命令（KernelSU→ksud，Magisk→magisk --install-module）
        var cmd = string.IsNullOrWhiteSpace(_installCmd)
            ? null
            : _installCmd!.Replace("{0}", Sh.Q(remote));

        // ② 没识别出实现时，仍尝试 ksud（老路径，兼容各种 fork）
        if (cmd == null)
        {
            var probe = await _fs.RunCommandAsync(
                $"command -v ksud || ls {DevicePaths.AdbData}/ksud 2>/dev/null", true, ct)
                .ConfigureAwait(false);
            if (probe.Contains("ksud", StringComparison.OrdinalIgnoreCase))
                cmd = "ksud module install " + Sh.Q(remote);
        }

        onLog(cmd != null ? $"→ 开始安装（{cmd}）…" : "→ 没有可用的安装器，改用直接部署…");
        onLog("");

        var code = cmd != null
            ? await _adb.RunStreamingAsync(
                new[] { "-s", _fs.Serial, "shell", "su", "-c", cmd + " 2>&1" },
                onLog, ct).ConfigureAwait(false)
            : await DeployDirectAsync(remote, onLog, ct).ConfigureAwait(false);

        // ③ 管理器安装失败时，再退回"直接部署"兜底（modules_update + .update 是通用机制）
        if (code != 0 && cmd != null)
        {
            onLog("");
            onLog("× 管理器安装失败，改用直接部署重试…");
            code = await DeployDirectAsync(remote, onLog, ct).ConfigureAwait(false);
        }

        onLog("");
        onLog(code == 0 ? "√ 安装流程结束" : $"× 安装返回码 {code}");

        await _adb.RunAsync(new[] { "-s", _fs.Serial, "shell", "su", "-c", "rm -f " + Sh.Q(remote) }, ct).ConfigureAwait(false);
        return code;
    }

    /// <summary>
    /// 不依赖 ksud / 管理器的安装方式：
    /// 解压到 /data/adb/modules_update/&lt;id&gt; 并打 .update 标记，重启时由内核侧合并。
    /// </summary>
    private async Task<int> DeployDirectAsync(string remote, Action<string> onLog, CancellationToken ct)
    {
        var tmp = DevicePaths.TempDirectory("mod");

        onLog("→ 解压模块包…");
        await _fs.RunCommandAsync(
            $"rm -rf {tmp}; mkdir -p {tmp}; unzip -o {Sh.Q(remote)} -d {tmp} >/dev/null 2>&1; echo DONE",
            true, ct).ConfigureAwait(false);

        var idRaw = await _fs.RunCommandAsync(
            $"sed -n 's/^id=//p' {tmp}/module.prop 2>/dev/null | head -1", true, ct).ConfigureAwait(false);
        var mm = System.Text.RegularExpressions.Regex.Match(idRaw, @"(?m)^\s*([A-Za-z0-9_.\-]+)\s*$");
        if (!mm.Success)
        {
            onLog("× 模块包缺少有效的 id=，已中止");
            await _fs.RunCommandAsync($"rm -rf {tmp}; rm -f {Sh.Q(remote)}", true, ct).ConfigureAwait(false);
            return -2;
        }

        var id = mm.Groups[1].Value;
        var target = DevicePaths.ModuleUpdateDir(id);
        onLog($"→ 模块 ID：{id}，写入 {target}");

        await _fs.RunCommandAsync($"rm -rf {Sh.Q(target)}", true, ct).ConfigureAwait(false);
        var cp = await _fs.RunCommandAsync(
            $"mkdir -p {Sh.Q(target)} && " +
            $"cp -a {tmp}/. {Sh.Q(target)}/ && " +
            $"chown -R 0:0 {Sh.Q(target)} && " +
            $"find {Sh.Q(target)} -type d -exec chmod 755 {{}} \\; && " +
            $"find {Sh.Q(target)} -type f -exec chmod 644 {{}} \\; && echo OK",
            true, ct).ConfigureAwait(false);

        var ok = cp.Contains("OK", StringComparison.Ordinal);
        if (ok)
        {
            // 关键：.update 标记让内核侧在下次开机的 post-fs-data 阶段合并
            await _fs.RunCommandAsync(
                $"mkdir -p {DevicePaths.ModulesUpdate} && touch {DevicePaths.ModulesUpdate}/.update",
                true, ct).ConfigureAwait(false);
            onLog("√ 已写入待更新队列，重启后生效");
        }
        else
        {
            onLog("× 写入失败：" + cp.Trim());
        }

        await _fs.RunCommandAsync($"rm -rf {tmp}; rm -f {Sh.Q(remote)}", true, ct).ConfigureAwait(false);
        return ok ? 0 : -3;
    }

    /// <summary>发送按键（刷模块时有些脚本要按音量/电源）</summary>
    public Task SendKeyAsync(int keyCode, CancellationToken ct = default)
        => _fs.RunCommandAsync($"input keyevent {keyCode}", false, ct);
}
