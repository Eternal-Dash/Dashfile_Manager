using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DashFile.Models;

namespace DashFile.Services;

public sealed class RemoteFileSystem
{
    private readonly AdbRunner _adb;
    public string Serial { get; }
    public bool UseRoot { get; set; }

    public event Action<string>? Log;

    public RemoteFileSystem(AdbRunner adb, string serial, bool useRoot)
    {
        _adb = adb;
        Serial = serial;
        UseRoot = useRoot;
    }

    private void L(string msg) => Log?.Invoke(msg);

    // ---------- 底层执行 ----------

    private async Task<ProcResult> ShellAsync(string command, bool? root = null, CancellationToken ct = default,
        bool quiet = false)
    {
        var useRoot = root ?? UseRoot;
        var args = new List<string> { "-s", Serial, "shell" };
        if (useRoot)
        {
            args.Add("su");
            args.Add("-c");
            args.Add(Sh.Q(command));
        }
        else
        {
            args.Add(command);
        }
        if (!quiet) L((useRoot ? "[root] " : "[shell] ") + command);
        return await _adb.RunAsync(args, ct).ConfigureAwait(false);
    }

    public async Task<bool> DetectRootAsync(CancellationToken ct = default)
    {
        // su 第一次调用可能因为授权弹窗返回失败，这里多试几种方式 + 重试
        for (int attempt = 0; attempt < 4; attempt++)
        {
            foreach (var cmd in new[] { "id", "whoami" })
            {
                var r = await _adb.RunAsync(new[] { "-s", Serial, "shell", "su", "-c", cmd }, ct).ConfigureAwait(false);
                var o = (r.StdOut + r.StdErr);
                if (o.Contains("uid=0") || o.Trim() == "root") return true;
            }

            var r2 = await _adb.RunAsync(new[] { "-s", Serial, "shell", "su", "0", "id" }, ct).ConfigureAwait(false);
            if ((r2.StdOut + r2.StdErr).Contains("uid=0")) return true;

            // 有些实现需要 -c 后整体加引号；再兜底判断 /data/adb 是否可列出（只有 root 能读）
            var r3 = await _adb.RunAsync(new[] { "-s", Serial, "shell", "su", "-c", $"ls {DevicePaths.KsuDir}/bin" }, ct).ConfigureAwait(false);
            if (r3.Ok && r3.StdOut.Contains("resetprop")) return true;

            try { await Task.Delay(600, ct); } catch { return false; }
        }
        return false;
    }

    // ---------- 列目录 ----------

    private static readonly Regex RxZ = new(
        @"^(?<p>[bcdlps-][rwxsStT-]{9})\s+\d+\s+(?<u>\S+)\s+(?<g>\S+)\s+(?<c>\S+)\s+(?<sz>\d+)\s+(?<d>\d{4}-\d{2}-\d{2})\s+(?<t>\d{2}:\d{2}(?::\d{2})?)\s+(?<n>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex RxNoZ = new(
        @"^(?<p>[bcdlps-][rwxsStT-]{9})\s+\d+\s+(?<u>\S+)\s+(?<g>\S+)\s+(?<sz>\d+)\s+(?<d>\d{4}-\d{2}-\d{2})\s+(?<t>\d{2}:\d{2}(?::\d{2})?)\s+(?<n>.+)$",
        RegexOptions.Compiled);

    public async Task<List<PosixEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        // 关键：目录加尾斜杠，否则 /sdcard 这类符号链接只会列出链接本身
        var dirPath = path.EndsWith("/") ? path : path + "/";
        var cmd = "LC_ALL=C ls -l -Z -- " + Sh.Q(dirPath);
        var r = await ShellAsync(cmd, ct: ct).ConfigureAwait(false);

        if (r.ExitCode != 0 && string.IsNullOrWhiteSpace(r.StdOut))
        {
            // 再试不带 -Z（部分目录/FUSE 不支持上下文）
            var r2 = await ShellAsync("LC_ALL=C ls -l -- " + Sh.Q(dirPath), ct: ct).ConfigureAwait(false);
            if (r2.ExitCode != 0 && string.IsNullOrWhiteSpace(r2.StdOut))
            {
                var detail = string.IsNullOrWhiteSpace(r2.All) ? "ls 没有返回内容（目录不存在或无权限）" : r2.All.Trim();
                throw new IOException(detail);
            }
            return Parse(r2.StdOut, path, false);
        }
        return Parse(r.StdOut, path, true);
    }

    /// <summary>
    /// 只列子目录（目录树专用）：`ls -1p` 不输出权限/SELinux/日期，设备端和解析都更轻。
    /// 拿不到时退回完整 <see cref="ListAsync"/> 再筛目录，行为保持一致。
    /// </summary>
    public async Task<List<PosixEntry>> ListDirsAsync(string path, CancellationToken ct = default)
    {
        var dirPath = path.EndsWith("/") ? path : path + "/";
        var r = await ShellAsync("LC_ALL=C ls -1p -- " + Sh.Q(dirPath), ct: ct).ConfigureAwait(false);

        if (r.ExitCode != 0 && string.IsNullOrWhiteSpace(r.StdOut))
        {
            var full = await ListAsync(path, ct).ConfigureAwait(false);
            return full.Where(e => e.IsDir).ToList();
        }

        var list = new List<PosixEntry>();
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[^1] != '/') continue;   // -p：只有目录带尾斜杠

            var name = line[..^1];
            if (name is "." or "..") continue;

            list.Add(new PosixEntry
            {
                Name = name,
                FullPath = Sh.Join(path, name),
                TypeChar = 'd',
            });
        }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private List<PosixEntry> Parse(string text, string dir, bool withCtx)    {
        var list = new List<PosixEntry>();
        var rx = withCtx ? RxZ : RxNoZ;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("total ", StringComparison.OrdinalIgnoreCase)) continue;
            var m = rx.Match(line);
            if (!m.Success) continue;

            var name = m.Groups["n"].Value;
            string? linkTo = null;
            if (m.Groups["p"].Value[0] == 'l')
            {
                var idx = name.IndexOf(" -> ", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    linkTo = name[(idx + 4)..];
                    name = name[..idx];
                }
            }

            DateTime dt = DateTime.MinValue;
            try
            {
                dt = DateTime.ParseExact(m.Groups["d"].Value + " " + m.Groups["t"].Value,
                    m.Groups["t"].Value.Length == 5 ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture);
            }
            catch { }

            list.Add(new PosixEntry
            {
                Name = name,
                FullPath = Sh.Join(dir, name),
                TypeChar = m.Groups["p"].Value[0],
                Perms = m.Groups["p"].Value[1..],
                Owner = m.Groups["u"].Value,
                Group = m.Groups["g"].Value,
                Selinux = withCtx ? m.Groups["c"].Value : "",
                Size = long.TryParse(m.Groups["sz"].Value, out var sz) ? sz : 0,
                Modified = dt,
                LinkTarget = linkTo ?? "",
            });
        }

        return list
            .OrderByDescending(e => e.IsDir)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------- 增删改 ----------

    public async Task MkdirAsync(string path, CancellationToken ct = default)
    {
        var r = await ShellAsync("mkdir -p -- " + Sh.Q(path) + " && chmod 755 -- " + Sh.Q(path), ct: ct).ConfigureAwait(false);
        EnsureOk(r);
    }

    public async Task TouchAsync(string path, CancellationToken ct = default)
    {
        var r = await ShellAsync("touch -- " + Sh.Q(path) + " && chmod 644 -- " + Sh.Q(path), ct: ct).ConfigureAwait(false);
        EnsureOk(r);
    }

    public async Task DeleteAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var all = paths.ToList();
        if (all.Count == 0) return;
        var joined = string.Join(" ", all.Select(Sh.Q));
        var r = await ShellAsync("rm -rf -- " + joined, ct: ct).ConfigureAwait(false);
        EnsureOk(r);
    }

    public async Task RenameAsync(string path, string newName, CancellationToken ct = default)
    {
        var target = Sh.Join(Sh.Parent(path), newName);
        var r = await ShellAsync("mv -f -- " + Sh.Q(path) + " " + Sh.Q(target), ct: ct).ConfigureAwait(false);
        EnsureOk(r);
    }

    public async Task ChmodAsync(string path, string octal, CancellationToken ct = default)
    {
        var r = await ShellAsync("chmod " + octal + " -- " + Sh.Q(path), ct: ct).ConfigureAwait(false);
        EnsureOk(r);
    }

    public async Task ChownAsync(string path, string owner, string group, CancellationToken ct = default)
    {
        var spec = string.IsNullOrWhiteSpace(group) ? owner : owner + ":" + group;
        var r = await ShellAsync("chown " + spec + " -- " + Sh.Q(path), ct: ct).ConfigureAwait(false);
        EnsureOk(r);
    }

    // ---------- SELinux ----------

    public async Task<string> RestoreconPreviewAsync(string path, CancellationToken ct = default)
    {
        var r = await ShellAsync("restorecon -nRv " + Sh.Q(path) + " 2>&1", ct: ct).ConfigureAwait(false);
        return r.All.Trim();
    }

    public async Task<string> RestoreconAsync(string path, CancellationToken ct = default)
    {
        var r = await ShellAsync("restorecon -Rv " + Sh.Q(path) + " 2>&1", ct: ct).ConfigureAwait(false);
        return r.All.Trim();
    }

    public async Task<string> ChconAsync(string path, string type, CancellationToken ct = default)
    {
        var ctx = "u:object_r:" + type + ":s0";
        var r = await ShellAsync("toybox chcon " + ctx + " -- " + Sh.Q(path) + " 2>&1", ct: ct).ConfigureAwait(false);
        return r.All.Trim();
    }

    // ---------- 传输 ----------

    public async Task UploadAsync(string localFile, string remoteDir, CancellationToken ct = default)
    {
        var name = Path.GetFileName(localFile);
        var tmp = DevicePaths.TempFile("up");
        L("[push] " + localFile + " -> " + tmp);

        var r1 = await _adb.RunAsync(new[] { "-s", Serial, "push", localFile, tmp }, _adb.TransferTimeout, ct).ConfigureAwait(false);
        if (!r1.Ok) throw new IOException(r1.All);

        var target = Sh.Join(remoteDir, name);
        // 不能用 "cp && chmod ; restorecon ; rm" 这种混链：尾部 rm 的退出码会把 cp 的失败盖掉，
        //   权限不足/磁盘满也会显示"上传成功"。改成整条 && 链，只有全成功才 echo DF_OK。
        var cmd = "cp -f " + Sh.Q(tmp) + " " + Sh.Q(target) +
                  " && chmod 644 " + Sh.Q(target) +
                  " && { restorecon " + Sh.Q(target) + " >/dev/null 2>&1; rm -f " + Sh.Q(tmp) + "; echo DF_OK; }";
        var r2 = await ShellAsync(cmd, ct: ct).ConfigureAwait(false);
        EnsureOk(r2);
        if (!r2.StdOut.Contains("DF_OK", StringComparison.Ordinal))
            throw new IOException(string.IsNullOrWhiteSpace(r2.All) ? "上传失败：目标不可写" : r2.All);
    }

    public async Task UploadBytesAsync(byte[] data, string remotePath, CancellationToken ct = default)
    {
        var tmpLocal = DevicePaths.LocalTempFile("bytes");
        try
        {
            await File.WriteAllBytesAsync(tmpLocal, data, ct).ConfigureAwait(false);
            var tmpRemote = DevicePaths.TempFile("bytes");
            var r1 = await _adb.RunAsync(new[] { "-s", Serial, "push", tmpLocal, tmpRemote }, _adb.TransferTimeout, ct).ConfigureAwait(false);
            if (!r1.Ok) throw new IOException(r1.All);
            var cmd = "cp -f " + Sh.Q(tmpRemote) + " " + Sh.Q(remotePath) +
                      " && { restorecon " + Sh.Q(remotePath) + " >/dev/null 2>&1; rm -f " + Sh.Q(tmpRemote) + "; echo DF_OK; }";
            var r2 = await ShellAsync(cmd, ct: ct).ConfigureAwait(false);
            EnsureOk(r2);
            if (!r2.StdOut.Contains("DF_OK", StringComparison.Ordinal))
                throw new IOException(string.IsNullOrWhiteSpace(r2.All) ? "写入失败：目标不可写" : r2.All);
        }
        finally
        {
            try { File.Delete(tmpLocal); } catch { }
        }
    }

    public async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct = default)
    {
        // 关键：不能用 exec-out + su -c + 引号（引号会被吃掉导致 cat 失败）
        // 改用 su 0 cat -- <path>，参数分开传，二进制安全
        var args = new List<string> { "-s", Serial, "exec-out" };
        if (UseRoot)
        {
            args.Add("su");
            args.Add("0");
        }
        args.Add("cat");
        args.Add("--");
        args.Add(remotePath);
        var bytes = await _adb.ExecOutAsync(args, ct).ConfigureAwait(false);

        // 以前不管成功与否都把拿到的字节写盘：cat 失败（权限/文件不存在）也留下一个 0 字节文件，
        //   UI 显示"下载成功"。现在 0 字节时必须确认 exit=0 且 stderr 干净，否则按失败抛出。
        if (bytes.Length == 0)
        {
            var err = (_adb.LastExecError ?? "").Trim();
            if (_adb.LastExecExitCode != 0 || err.Length > 0)
                throw new IOException("下载失败（exit " + _adb.LastExecExitCode + "）：" +
                                      (err.Length > 0 ? err : "远端返回空"));
        }

        await File.WriteAllBytesAsync(localPath, bytes, ct).ConfigureAwait(false);
    }

    // ---------- 文本 ----------

    public async Task<string> ReadTextAsync(string path, CancellationToken ct = default)
    {
        var r = await ShellAsync("cat -- " + Sh.Q(path), ct: ct).ConfigureAwait(false);
        EnsureOk(r);
        return r.StdOut;
    }

    public Task WriteTextAsync(string path, string content, CancellationToken ct = default)
        => UploadBytesAsync(Encoding.UTF8.GetBytes(content), path, ct);

    // ---------- 辅助 ----------

    private static void EnsureOk(ProcResult r)
    {
        if (!r.Ok) throw new IOException(string.IsNullOrWhiteSpace(r.StdErr) ? r.All : r.StdErr);
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var r = await ShellAsync("[ -e " + Sh.Q(path) + " ] && echo 1 || echo 0", ct: ct).ConfigureAwait(false);
        return r.StdOut.Trim() == "1";
    }

    // ---------- 执行 ----------

    public async Task<string> RunCommandAsync(string command, bool useRoot, CancellationToken ct = default,
        bool quiet = false)
    {
        var r = await ShellAsync(command, useRoot, ct, quiet).ConfigureAwait(false);
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(r.StdOut)) sb.AppendLine(r.StdOut.TrimEnd());
        if (!string.IsNullOrWhiteSpace(r.StdErr)) sb.AppendLine(r.StdErr.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("[退出码] " + r.ExitCode);
        return sb.ToString();
    }

    /// <summary>给插件 API 用的结构化执行入口（保留 stdout / stderr / exit code）。</summary>
    public Task<ProcResult> ExecuteAsync(string command, bool? root = null, CancellationToken ct = default)
        => ShellAsync(command, root, ct);

    /// <summary>
    /// 以 argv 方式执行设备端程序（不经过 shell）。需要 root 时使用 `su 0 &lt;exe&gt; args`，
    /// 这样参数里的空格/引号不会被二次解释。
    /// </summary>
    public async Task<ProcResult> RunArgvAsync(string executable, IEnumerable<string> args, bool? root = null,
        CancellationToken ct = default)
    {
        var list = new List<string> { "-s", Serial, "shell" };
        if (root ?? UseRoot)
        {
            list.Add("su");
            list.Add("0");
        }
        list.Add(executable);
        list.AddRange(args);
        return await _adb.RunAsync(list, ct).ConfigureAwait(false);
    }

    /// <summary>把本机文件推到 /data/local/tmp，返回远端临时路径（已 chmod 644）。插件 API 使用。</summary>
    public async Task<string> PushTempFileAsync(string localPath, CancellationToken ct = default)
    {
        var remote = DevicePaths.TempFile("plugin", Path.GetExtension(localPath));
        var r = await _adb.RunAsync(new[] { "-s", Serial, "push", localPath, remote }, _adb.TransferTimeout, ct)
                          .ConfigureAwait(false);
        if (!r.Ok) throw new IOException(r.All);
        await ShellAsync("chmod 644 " + Sh.Q(remote), ct: ct).ConfigureAwait(false);
        return remote;
    }

    /// <summary>
    /// 直接以 argv 方式执行（不经过 shell），用于取二进制内容，避免引号被吞。
    /// </summary>
    public async Task<byte[]> ExecCaptureArgsAsync(string exe, IEnumerable<string> args, bool? root = null, CancellationToken ct = default)
    {
        var list = new List<string> { "-s", Serial, "exec-out" };
        if (root ?? UseRoot)
        {
            list.Add("su");
            list.Add("0");
        }
        list.Add(exe);
        list.AddRange(args);
        return await _adb.ExecOutAsync(list, ct).ConfigureAwait(false);
    }

    /// <summary>执行命令并把 stdout 当二进制取回（取图标等）</summary>
    public async Task<byte[]> ExecCaptureAsync(string command, bool? root = null, CancellationToken ct = default)
    {
        var args = new List<string> { "-s", Serial, "exec-out" };
        if (root ?? UseRoot)
        {
            args.Add("su");
            args.Add("0");
        }
        args.Add("sh");
        args.Add("-c");
        args.Add(Sh.Q(command));
        return await _adb.ExecOutAsync(args, ct).ConfigureAwait(false);
    }

    // ---------- 拖出用：把远端目录打包 ----------

    public async Task<string> TarGzAsync(string remoteDir, string remoteOut, CancellationToken ct = default)
    {
        var parent = Sh.Parent(remoteDir);
        var name = Sh.BaseName(remoteDir);
        var cmd = "tar -czf " + Sh.Q(remoteOut) + " -C " + Sh.Q(parent) + " " + Sh.Q(name) + " 2>&1";
        var r = await ShellAsync(cmd, ct: ct).ConfigureAwait(false);
        if (!r.Ok) throw new IOException(r.All);
        return remoteOut;
    }

    /// <summary>以二进制方式读取远端文件（su 0 cat，二进制安全）</summary>
    public async Task<byte[]> ReadBytesAsync(string remotePath, CancellationToken ct = default)
    {
        var args = new List<string> { "-s", Serial, "exec-out" };
        if (UseRoot) { args.Add("su"); args.Add("0"); }
        args.Add("cat");
        args.Add("--");
        args.Add(remotePath);
        return await _adb.ExecOutAsync(args, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 覆盖写入远端文件。注意：adb push 是以 shell（uid 2000）身份跑的，
    /// 写 /data/adb 这类 root-only 目录必须先推到 /data/local/tmp，再用 su 搬过去。
    /// </summary>
    public async Task<bool> WriteBytesAsync(string remotePath, byte[] data, CancellationToken ct = default)
    {
        var localTmp = DevicePaths.LocalTempFile("w", ".bin");
        var remoteTmp = DevicePaths.TempFile("w", ".bin");
        try
        {
            await File.WriteAllBytesAsync(localTmp, data, ct).ConfigureAwait(false);

            var push = await _adb.RunAsync(new[] { "-s", Serial, "push", localTmp, remoteTmp }, _adb.TransferTimeout, ct)
                                  .ConfigureAwait(false);
            if (!push.Ok) return false;

            // 该目录 SELinux 策略禁止 mv/unlink，只能原地覆盖
            var r = await RunCommandAsync(
                $"cp -f {Sh.Q(remoteTmp)} {Sh.Q(remotePath)} && " +
                $"chmod 644 {Sh.Q(remotePath)} && rm -f {Sh.Q(remoteTmp)} && echo DF_OK",
                true, ct).ConfigureAwait(false);

            return r.Contains("DF_OK", StringComparison.Ordinal);
        }
        catch { return false; }
        finally { try { File.Delete(localTmp); } catch { } }
    }
}
