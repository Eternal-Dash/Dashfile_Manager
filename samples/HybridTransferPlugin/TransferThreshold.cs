using System.Diagnostics;
using System.Globalization;
using DashFile.PluginApi;

namespace HybridTransferPlugin;

public static class TransferThreshold
{
    public const long Megabyte = 1024L * 1024;
    public const long Gigabyte = 1024 * Megabyte;
    public const long DefaultBytes = Gigabyte;
    public const long MaximumBytes = HybridSession.MaximumFile;

    public static bool ShouldUseHybrid(long size, long threshold) => size >= 0 && size >= threshold;
    public static long Normalize(long bytes) => bytes < Megabyte || bytes > MaximumBytes ? DefaultBytes : bytes / Megabyte * Megabyte;
    public static long FromDisplay(decimal value, bool gigabytes)
    {
        var mb = decimal.Round(value * (gigabytes ? 1024 : 1), 0, MidpointRounding.AwayFromZero);
        return (long)Math.Clamp(mb, 1, MaximumBytes / Megabyte) * Megabyte;
    }
    public static string Describe(long bytes) => bytes % Gigabyte == 0
        ? $"{bytes / Gigabyte} GB（{bytes / Megabyte} MB）" : $"{bytes / Megabyte} MB";

    // Size checks precede the temporary Java service, Wi-Fi probing, and all target writes.
    // Failed/unsupported stat is a preflight failure: the host retains its ordinary ADB path.
    public static async Task<long> ReadSizeAsync(PluginTransferRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Direction == PluginTransferDirection.Upload) return new FileInfo(request.LocalPath).Length;
        using var deadline = HybridSession.Deadline(ct, 20);
        var path = PluginShell.Quote(request.RemotePath);
        var command = "[ -f " + path + " ] && stat -L -c %s -- " + path;
        var info = new ProcessStartInfo(request.AdbPath) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        foreach(var arg in new[]{"-s",request.Serial,"shell"}) info.ArgumentList.Add(arg);
        if(request.UseRoot) { info.ArgumentList.Add("su");info.ArgumentList.Add("-c");info.ArgumentList.Add(PluginShell.Quote(command)); }
        else info.ArgumentList.Add(command);
        using var process = new Process { StartInfo=info };
        process.Start();
        using var cancel = deadline.Token.Register(() => { try { if(!process.HasExited)process.Kill(true); } catch { } });
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await Task.WhenAll(output,error,process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            if(process.ExitCode != 0 || !long.TryParse((await output.ConfigureAwait(false)).Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out var size))
                throw new IOException("无法确认设备文件大小");
            return size;
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested) { throw new TimeoutException("查询文件大小超时"); }
        finally { try { if(!process.HasExited)process.Kill(true); } catch { } }
    }
}
