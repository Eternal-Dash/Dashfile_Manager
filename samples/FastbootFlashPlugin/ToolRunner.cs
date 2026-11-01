using System.Diagnostics;
using System.Text;

namespace FastbootFlashPlugin;

public enum Transport { Fastboot, AdbRoot }
public sealed record DeviceTarget(string Serial, Transport Transport)
{
    public string Display => Serial + (Transport == Transport.Fastboot ? "  ·  Fastboot" : "  ·  ADB（回读）");
}
public sealed record ToolResult(int ExitCode, string Output) { public bool Ok => ExitCode == 0; }
public interface IToolRunner
{
    Task<ToolResult> RunAsync(Transport transport, IReadOnlyList<string> args, string? stdoutFile,
        TimeSpan? timeout, Action<string>? line, CancellationToken cancellationToken);
}

/// <summary>No shell, no argument concatenation; binary stdout is streamed directly to disk.</summary>
public sealed class NativeToolRunner : IToolRunner
{
    public string FastbootPath { get; }
    public string AdbPath { get; }
    public NativeToolRunner(string? fastboot = null, string? adb = null)
    {
        FastbootPath = fastboot ?? Resolve("fastboot.exe", "Fastboot");
        AdbPath = adb ?? Resolve("adb.exe", "Adb");
    }
    private static string Resolve(string name, string sub)
    {
        foreach (var path in new[] {
            Path.Combine(AppContext.BaseDirectory, "Binaries", sub, name),
            Path.Combine(AppContext.BaseDirectory, "Binaries", name), Path.Combine(AppContext.BaseDirectory, name),
            Path.Combine(AppContext.BaseDirectory, "tools", "adb", name) })
            if (File.Exists(path)) return path;
        // ProcessStartInfo still invokes only an executable, never cmd.exe/PowerShell.
        return name;
    }
    public async Task<ToolResult> RunAsync(Transport transport, IReadOnlyList<string> args, string? stdoutFile,
        TimeSpan? timeout, Action<string>? line, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(transport == Transport.Fastboot ? FastbootPath : AdbPath)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout.HasValue) cancel.CancelAfter(timeout.Value);
        var log = new StringBuilder();
        var gate = new object();
        async Task Pump(StreamReader reader)
        {
            while (await reader.ReadLineAsync(cancel.Token).ConfigureAwait(false) is { } text)
            {
                lock (gate) { if (log.Length < 256_000) log.AppendLine(text); }
                try { line?.Invoke(text); } catch { /* logging must not stop a flash */ }
            }
        }
        process.Start();
        using var registration = cancel.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        Task stdout;
        FileStream? file = null;
        try
        {
            if (stdoutFile == null) stdout = Pump(process.StandardOutput);
            else
            {
                file = new FileStream(stdoutFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                stdout = process.StandardOutput.BaseStream.CopyToAsync(file, cancel.Token);
            }
            var stderr = Pump(process.StandardError);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(cancel.Token)).ConfigureAwait(false);
            if (file != null) await file.FlushAsync(cancel.Token).ConfigureAwait(false);
            return new ToolResult(process.ExitCode, log.ToString());
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
        finally { if (file != null) await file.DisposeAsync().ConfigureAwait(false); }
    }
}
