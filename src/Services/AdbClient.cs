using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DashFile.Models;

namespace DashFile.Services;

public sealed class AdbClient
{
    private readonly AdbRunner _adb;
    public AdbClient(AdbRunner adb) => _adb = adb;

    public async Task<(bool ok, string message)> EnsureServerAsync(CancellationToken ct = default)
    {
        // 关键：如果已经有 adb server 在跑，就不要再去 start-server
        //   否则 adb 客户端版本不一致时会杀掉旧服务，导致设备掉线且必须重新插拔
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            // 旧写法 Task.WhenAny(connect, Delay) 超时后 connect 被弃置，socket 释放时
            //   会抛 SocketException(995)，变成未观察的 Task 异常（crash-*.log 的根因）。
            //   这里让 ConnectAsync 自己带 300ms 取消，并 await 到结束再 dispose。
            await probe.ConnectAsync("127.0.0.1", 5037, timeout.Token).ConfigureAwait(false);
            if (probe.Connected)
                return (true, "server already running");
        }
        catch (OperationCanceledException) { }
        catch (System.Net.Sockets.SocketException) { }

        var r = await _adb.RunAsync(new[] { "start-server" }, ct).ConfigureAwait(false);
        return (r.Ok, r.All.Trim());
    }

    public async Task<List<DeviceInfo>> ListDevicesAsync(CancellationToken ct = default)
    {
        var list = new List<DeviceInfo>();
        var r = await _adb.RunAsync(new[] { "devices", "-l" }, ct).ConfigureAwait(false);
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("*")) continue;
            var d = DeviceInfo.Parse(line);
            if (d != null) list.Add(d);
        }
        return list;
    }

    public async Task<string> GetPropAsync(string serial, string key, CancellationToken ct = default)
    {
        var r = await _adb.RunAsync(new[] { "-s", serial, "shell", "getprop", key }, ct).ConfigureAwait(false);
        return r.StdOut.Trim();
    }

    /// <summary>adb server 可能卡在陈旧状态（设备已插但列表为空），需要重启服务</summary>
    public async Task ResetServerAsync(CancellationToken ct = default)
    {
        await _adb.RunAsync(new[] { "kill-server" }, ct).ConfigureAwait(false);
        try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { }
        await _adb.RunAsync(new[] { "start-server" }, ct).ConfigureAwait(false);
        try { await Task.Delay(800, ct).ConfigureAwait(false); } catch { }
    }
}
