using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DashFile.Services;

public enum DeviceMode { None, System, Recovery, Fastboot, Fastbootd }
public enum RebootTarget { System, Bootloader, Fastbootd, Recovery, Edl }

public sealed class PowerService
{
    private readonly AdbRunner _adb;
    public PowerService(AdbRunner adb) => _adb = adb;

    private static string ToolPath(string name)
    {
        var dir = AppContext.BaseDirectory;
        foreach (var d in new[]
        {
            Path.Combine(dir, "Binaries", name == "fastboot" ? "Fastboot" : "Adb"),
            Path.Combine(dir, "Binaries"),
            dir,
            Path.Combine(dir, "tools"),
        })
        {
            var p = Path.Combine(d, name + ".exe");
            if (File.Exists(p)) return p;
        }
        return name;
    }

    private static async Task<string> RunToolAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return "";
            var so = p.StandardOutput.ReadToEndAsync(ct);
            var se = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return (await so) + (await se);
        }
        catch { return ""; }
    }

    /// <summary>
    /// 列出当前处于 fastboot / fastbootd 的设备序列号。
    /// 手机重启到 fastboot 之后就不再是 ADB 设备了，只查 adb 会以为"设备没了"。
    /// </summary>
    public async Task<List<string>> ListFastbootSerialsAsync(CancellationToken ct = default)
    {
        var result = new List<string>();
        try
        {
            var outp = await RunToolAsync(ToolPath("fastboot"), new[] { "devices" }, ct).ConfigureAwait(false);
            foreach (var raw in outp.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
                if (!line.Contains("fastboot", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("sideload", StringComparison.OrdinalIgnoreCase)) continue;

                var serial = line.Split('\t', ' ').FirstOrDefault()?.Trim() ?? "";
                if (serial.Length > 0 && !result.Contains(serial)) result.Add(serial);
            }
        }
        catch { }
        return result;
    }

    public async Task<DeviceMode> DetectModeAsync(string serial, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(serial)) return DeviceMode.None;

        // ① fastboot / fastbootd
        var fb = await RunToolAsync(ToolPath("fastboot"), new[] { "devices" }, ct).ConfigureAwait(false);
        if (fb.Contains(serial, StringComparison.OrdinalIgnoreCase))
        {
            var uv = await RunToolAsync(ToolPath("fastboot"),
                new[] { "-s", serial, "getvar", "is-userspace" }, ct).ConfigureAwait(false);
            return uv.Contains("yes", StringComparison.OrdinalIgnoreCase) ? DeviceMode.Fastbootd : DeviceMode.Fastboot;
        }

        // ② adb：用 get-state 拿真实状态。
        //    不能用"adb devices 里出现这个序列号"就当 System —— recovery / sideload /
        //      unauthorized 都会出现，会被误判成系统模式，于是重启按钮发出无效命令。
        var gs = await _adb.RunAsync(new[] { "-s", serial, "get-state" }, ct).ConfigureAwait(false);
        var state = (gs.StdOut ?? "").Trim().ToLowerInvariant();
        var err = (gs.StdErr ?? "").ToLowerInvariant();

        // 关键：设备不在时 adb 输出的是 "error: device 'X' not found"，
        //   里面就带 "device" 这个词。以前用 Contains("device") 判断，会把"设备不存在"
        //   当成"已连接"，于是返回 System、发出无效重启命令 —— 表现就是"按什么都没用"。
        //   所以这里只认 get-state 的**整行精确值**，并显式识别 not found。
        var gone = err.Contains("not found") || err.Contains("no devices/emulators");

        if (!gone)
        {
            if (state == "recovery" || state == "sideload") return DeviceMode.Recovery;
            if (state == "bootloader") return DeviceMode.Fastboot;

            if (state == "device")
            {
                var boot = await _adb.RunAsync(new[] { "-s", serial, "shell", "getprop", "sys.boot_completed" }, ct).ConfigureAwait(false);
                if (boot.StdOut.Trim() == "1") return DeviceMode.System;

                var mode = await _adb.RunAsync(new[] { "-s", serial, "shell", "getprop", "ro.boot.mode" }, ct).ConfigureAwait(false);
                if (mode.StdOut.Contains("recovery", StringComparison.OrdinalIgnoreCase)) return DeviceMode.Recovery;

                return DeviceMode.System;      // adb 通但还没启动完
            }

            // ③ 兜底：adb devices 里有记录（比如 unauthorized），状态未知就按 Recovery 处理，
            //    至少让"重启"这条路还能走
            var dev = await _adb.RunAsync(new[] { "devices" }, ct).ConfigureAwait(false);
            if (dev.StdOut.Contains(serial, StringComparison.OrdinalIgnoreCase)) return DeviceMode.Recovery;
        }

        return DeviceMode.None;
    }

    public static string ModeText(DeviceMode m) => m switch
    {
        DeviceMode.System => "系统模式",
        DeviceMode.Recovery => "Recovery 模式",
        DeviceMode.Fastboot => "Fastboot 模式",
        DeviceMode.Fastbootd => "Fastbootd 模式",
        _ => "未连接",
    };

    public async Task RebootAsync(string serial, DeviceMode mode, RebootTarget target, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(serial)) return;

        if (mode is DeviceMode.Fastboot or DeviceMode.Fastbootd)
        {
            var args = target switch
            {
                RebootTarget.System => new[] { "-s", serial, "reboot" },
                RebootTarget.Bootloader => new[] { "-s", serial, "reboot", "bootloader" },
                RebootTarget.Fastbootd => new[] { "-s", serial, "reboot", "fastboot" },
                RebootTarget.Recovery => new[] { "-s", serial, "reboot", "recovery" },
                RebootTarget.Edl => new[] { "-s", serial, "oem", "edl" },
                _ => new[] { "-s", serial, "reboot" },
            };
            await RunToolAsync(ToolPath("fastboot"), args, ct).ConfigureAwait(false);
            return;
        }

        // 9008 比较特殊：不同 ROM 认不同的命令，逐个试，直到设备真的离线
        if (target == RebootTarget.Edl && mode is DeviceMode.System or DeviceMode.Recovery)
        {
            var attempts = new[]
            {
                new[] { "-s", serial, "shell", "reboot", "edl" },
                new[] { "-s", serial, "shell", "su", "0", "reboot", "edl" },
                new[] { "-s", serial, "shell", "su", "-c", Sh.Q("reboot edl") },
                new[] { "-s", serial, "shell", "su", "0", "setprop", "sys.powerctl", "reboot,edl" },
            };

            foreach (var a in attempts)
            {
                await _adb.RunAsync(a, ct).ConfigureAwait(false);
                await Task.Delay(700, ct).ConfigureAwait(false);

                var st = await _adb.RunAsync(new[] { "-s", serial, "get-state" }, ct).ConfigureAwait(false);
                var stateOut = st.StdOut.Trim();

                // 三种情况要分开：还在系统里 / 探测本身失败（超时、报错）/ 真的掉线。
                //   以前只看"输出里有没有 device"：get-state 超时或报错时 StdOut 也是空的，
                //   会被误判成"已进 9008"，余下的尝试全部放弃。
                if (stateOut.StartsWith("device", StringComparison.OrdinalIgnoreCase)) continue;   // 还在系统里
                if (stateOut.Length == 0 && (st.ExitCode == -1 || st.StdErr.Contains("超时", StringComparison.Ordinal)))
                    continue;                                                                     // 探测不可信，继续试
                return;                                                                            // 有明确响应且已不是 device
            }
            return;
        }

        // Recovery 里 adb shell 能力有限，用宿主端 `adb reboot <target>` 更可靠
        if (mode == DeviceMode.Recovery)
        {
            var hostArgs = target switch
            {
                RebootTarget.System => new[] { "-s", serial, "reboot" },
                RebootTarget.Bootloader => new[] { "-s", serial, "reboot", "bootloader" },
                RebootTarget.Fastbootd => new[] { "-s", serial, "reboot", "fastboot" },
                RebootTarget.Recovery => new[] { "-s", serial, "reboot", "recovery" },
                RebootTarget.Edl => new[] { "-s", serial, "reboot", "edl" },
                _ => new[] { "-s", serial, "reboot" },
            };
            await _adb.RunAsync(hostArgs, ct).ConfigureAwait(false);
            return;
        }

        // 系统 / Recovery 都走 adb reboot
        var cmd = target switch
        {
            RebootTarget.System => "reboot",
            RebootTarget.Bootloader => "reboot bootloader",
            RebootTarget.Fastbootd => "reboot fastboot",
            RebootTarget.Recovery => "reboot recovery",
            RebootTarget.Edl => "reboot edl",
            _ => "reboot",
        };

        var r = await _adb.RunAsync(new[] { "-s", serial, "shell", cmd }, ct).ConfigureAwait(false);
        if (!r.Ok)
        {
            // 普通权限下 reboot 可能被拒，改用 root
            // 必须整体加引号：adb shell 是空格拼接后交给设备端 sh，裸写 su -c reboot bootloader
            //   会让 su 只拿到 "reboot"，参数被丢掉，root 兜底静默失效。
            await _adb.RunAsync(new[] { "-s", serial, "shell", "su", "-c", Sh.Q(cmd) }, ct).ConfigureAwait(false);
        }
    }
}
