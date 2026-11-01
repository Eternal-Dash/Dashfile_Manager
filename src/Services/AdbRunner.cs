using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DashFile.Services;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string All => (StdOut ?? "") + (StdErr ?? "");
}

public sealed class AdbRunner
{
    public string AdbPath { get; }

    public AdbRunner(string adbPath) => AdbPath = adbPath;

    private ProcessStartInfo Base(IEnumerable<string> args, bool utf8)
    {
        var psi = new ProcessStartInfo(AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (utf8)
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>普通命令的超时（su 弹授权框时会一直挂着，必须有兜底）</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>大文件传输（push/pull/install）用的超时：20 秒会杀掉正常安装</summary>
    public TimeSpan TransferTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public Task<ProcResult> RunAsync(IEnumerable<string> args, CancellationToken ct = default)
        => RunAsync(args, Timeout, ct);

    /// <summary>指定本次超时（传输类命令用 TransferTimeout，别被普通命令的 20 秒兜底误杀）</summary>
    public async Task<ProcResult> RunAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        Process? p = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero) linked.CancelAfter(timeout);
        var token = linked.Token;

        try
        {
            p = Process.Start(Base(args, true));
            if (p == null) return new ProcResult(-1, "", "无法启动 adb");
            var so = p.StandardOutput.ReadToEndAsync(token);
            var se = p.StandardError.ReadToEndAsync(token);
            await p.WaitForExitAsync(token).ConfigureAwait(false);
            return new ProcResult(p.ExitCode, await so.ConfigureAwait(false), await se.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p?.Kill(true); } catch { }
            return new ProcResult(-1, "", timeout > TimeSpan.Zero && timeout < TimeSpan.FromMinutes(1)
                ? $"命令超时（{timeout.TotalSeconds:0}s）：可能手机正在等待授权，或设备无响应"
                : $"命令超时（{timeout.TotalMinutes:0.#} 分钟）：设备长时间无响应");
        }
        catch (OperationCanceledException)
        {
            try { p?.Kill(true); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            return new ProcResult(-1, "", ex.Message);
        }
        finally
        {
            p?.Dispose();
        }
    }

    /// <summary>下载用，给足时间（大文件），但仍防止无限挂起</summary>
    public TimeSpan ExecTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>安装 APK 用：比普通传输短（等 su 授权弹窗时用户能更早看到失败），但远大于 20 秒</summary>
    public TimeSpan InstallTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>最近一次 ExecOutAsync 的 stderr（成功时也可能有 warning，失败时才是关键线索）</summary>
    public string LastExecError { get; private set; } = "";

    /// <summary>最近一次 ExecOutAsync 的退出码（进程没起来/超时为 -1）</summary>
    public int LastExecExitCode { get; private set; }

    public async Task<byte[]> ExecOutAsync(IEnumerable<string> args, CancellationToken ct = default)
    {
        Process? p = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (ExecTimeout > TimeSpan.Zero) linked.CancelAfter(ExecTimeout);
        var token = linked.Token;
        LastExecError = "";
        LastExecExitCode = 0;

        try
        {
            p = Process.Start(Base(args, false));
            if (p == null) { LastExecExitCode = -1; LastExecError = "无法启动 adb"; return Array.Empty<byte>(); }
            using var ms = new MemoryStream();
            // 必须同时排空 stderr：Base() 里 RedirectStandardError=true，不读就会写满管道缓冲，
            //   子进程卡在写错误输出上，直到 10 分钟超时才被 Kill —— 而 su/cat 的真实报错也全丢了。
            var errTask = p.StandardError.ReadToEndAsync(token);
            await p.StandardOutput.BaseStream.CopyToAsync(ms, token).ConfigureAwait(false);
            await p.WaitForExitAsync(token).ConfigureAwait(false);
            LastExecError = await errTask.ConfigureAwait(false);
            LastExecExitCode = p.ExitCode;
            return ms.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p?.Kill(true); } catch { }
            LastExecExitCode = -1;
            LastExecError = $"读取超时（{ExecTimeout.TotalMinutes:0} 分钟）";
            throw new TimeoutException(LastExecError);
        }
        catch (OperationCanceledException)
        {
            try { p?.Kill(true); } catch { }
            throw;
        }
        finally
        {
            p?.Dispose();
        }
    }

    /// <summary>Streams binary stdout; process result belongs to this call, never shared LastExec state.</summary>
    public async Task<ProcResult> ExecOutToFileAsync(IEnumerable<string> args, string file, CancellationToken ct = default, Action<long>? progress = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (TransferTimeout > TimeSpan.Zero) linked.CancelAfter(TransferTimeout);
        using var process = new Process { StartInfo = Base(args, false) };
        if (!process.Start()) throw new IOException("无法启动 adb");
        using var registration = linked.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        Task<string>? errors = null;
        try
        {
            using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            errors = process.StandardError.ReadToEndAsync(linked.Token);
            async Task Copy()
            {
                var buffer=new byte[1024*1024];long copied=0;int n;
                while((n=await process.StandardOutput.BaseStream.ReadAsync(buffer,linked.Token).ConfigureAwait(false))>0)
                {await output.WriteAsync(buffer.AsMemory(0,n),linked.Token).ConfigureAwait(false);copied+=n;progress?.Invoke(copied);}
            }
            await Task.WhenAll(Copy(), process.WaitForExitAsync(linked.Token)).ConfigureAwait(false);
            await output.FlushAsync(linked.Token).ConfigureAwait(false);
            return new ProcResult(process.ExitCode, "", await errors.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("ADB 文件传输超时。"); }
        catch (IOException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false); } catch { }
            if(errors!=null)try{await errors.ConfigureAwait(false);}catch{}
        }
    }

    /// <summary>
    /// 边执行边回传输出（用于刷模块时滚动日志）。
    /// 必须也有超时：以前只依赖外部 ct，刷入脚本卡住（等音量键、等 su 授权）时
    ///   进程会一直挂着，用户只能强关窗口；这里复用 ExecTimeout 兜底。
    /// </summary>
    public async Task<int> RunStreamingAsync(IEnumerable<string> args, Action<string> onLine, CancellationToken ct = default)
    {
        Process? p = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (ExecTimeout > TimeSpan.Zero) linked.CancelAfter(ExecTimeout);
        var timedOut = false;
        try
        {
            var psi = Base(args, true);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return p.ExitCode;
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p?.Kill(true); } catch { }
            if (timedOut)
                onLine($"× 执行超时（{ExecTimeout.TotalMinutes:0} 分钟），已终止");
            return -1;
        }
        catch (Exception ex)
        {
            onLine("执行出错：" + ex.Message);
            return -1;
        }
        finally { p?.Dispose(); }
    }
}
