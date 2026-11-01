using System;
using System.Diagnostics;
using System.Threading;
using DashFile.PluginApi;

namespace DashFile.Services;

public sealed record TransferSnapshot(string FileName, string Direction, string Channels, string Phase,
    long Completed, long Total, double BytesPerSecond, double ElapsedSeconds, bool Cancelling, bool Finished, string Error);

/// <summary>One file, including preflight, transfer, commit, and cleanup. Finish only after cleanup awaits.</summary>
public sealed class FileTransferOperation : IProgress<PluginTransferProgress>, IDisposable
{
    private readonly object _gate=new();
    private readonly CancellationTokenSource _cancel;
    private readonly Stopwatch _clock=Stopwatch.StartNew();
    private string _channels="正在选择传输方式",_phase="准备中",_error="";
    private long _completed,_total,_sampleBytes;
    private double _sampleAt,_speed;
    private bool _finished;
    public PluginTransferRequest Request { get; }
    public CancellationToken Token=>_cancel.Token;
    public FileTransferOperation(PluginTransferRequest request,CancellationToken ct)
    {Request=request;_cancel=CancellationTokenSource.CreateLinkedTokenSource(ct);}
    public void Report(PluginTransferProgress value)
    {
        lock(_gate)
        {
            if(_finished)return;
            if(value.Phase==_phase&&value.CompletedBytes<_completed)return;
            var now=_clock.Elapsed.TotalSeconds;
            if(value.CompletedBytes<_completed || value.Phase!=_phase)
            {_sampleAt=now;_sampleBytes=value.CompletedBytes;_speed=0;}
            else if(now-_sampleAt>=.3)
            {_speed=Math.Max(0,(value.CompletedBytes-_sampleBytes)/(now-_sampleAt));_sampleAt=now;_sampleBytes=value.CompletedBytes;}
            _completed=Math.Max(0,value.CompletedBytes);_total=Math.Max(0,value.TotalBytes);_channels=value.Channels;_phase=value.Phase;
        }
    }
    public void Cancel()
    {
        lock(_gate){if(_finished)return;_phase="正在停止并清理未完成文件…";_speed=0;}
        try{_cancel.Cancel();}catch(ObjectDisposedException){}
    }
    public TransferSnapshot Snapshot
    {
        get{lock(_gate)return new(System.IO.Path.GetFileName(Request.LocalPath),Request.Direction==PluginTransferDirection.Upload?"上传到设备":"下载到电脑",_channels,_phase,_completed,_total,
            _clock.Elapsed.TotalSeconds-_sampleAt>1.5?0:_speed,_clock.Elapsed.TotalSeconds,_cancel.IsCancellationRequested,_finished,_error);}
    }
    public void Finish(Exception? error=null)
    {
        lock(_gate)
        {
            _finished=true;_clock.Stop();_speed=0;
            _error=error is null or OperationCanceledException?"":error.Message;
            _phase=error==null?"传输完成":error is OperationCanceledException?"已取消，未完成文件已清理":"传输未完成";
        }
    }
    public void Dispose()=>_cancel.Dispose();
}

public sealed class FileTransferOperations
{
    private int _active;
    public int ActiveCount=>Volatile.Read(ref _active);
    public event Action<FileTransferOperation>? Started;
    public FileTransferOperation Begin(PluginTransferRequest request,CancellationToken ct)
    {
        var operation=new FileTransferOperation(request,ct);Interlocked.Increment(ref _active);
        try{Started?.Invoke(operation);}catch{Interlocked.Decrement(ref _active);operation.Dispose();throw;}
        return operation;
    }
    public void End(FileTransferOperation operation,Exception? error)
    {operation.Finish(error);Interlocked.Decrement(ref _active);}
}
