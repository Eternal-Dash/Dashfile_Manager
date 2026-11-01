using DashFile.PluginApi;
namespace HybridTransferPlugin;

public sealed class HybridProvider(string dataDirectory,Action<string> log) : IPluginFileTransferProvider, IAsyncDisposable
{
    private readonly SemaphoreSlim _serial = new(1,1);
    private readonly object _stopGate=new();
    private CancellationTokenSource _stop=new();
    private AgentContext? _context;
    private string _key="";
    private int _active;
    private long _thresholdBytes=TransferThreshold.DefaultBytes;
    public long ThresholdBytes { get=>Interlocked.Read(ref _thresholdBytes); set=>Interlocked.Exchange(ref _thresholdBytes,TransferThreshold.Normalize(value)); }
    public string DisplayName => "双轨快传";
    public bool IsEnabled {get;set;}=true;
    public bool Wifi {get;set;}=true;
    public int MaxRails {get;set;}=3;
    public bool Active => Volatile.Read(ref _active)>0;
    public IReadOnlyDictionary<string,long> Traffic => _context?.Session?.Traffic ?? new Dictionary<string,long>();
    public string ChannelSummary => _context?.Session?.Channels ?? "尚未连接";
    public Task TransferAsync(PluginTransferRequest request,IProgress<PluginTransferProgress>? progress,CancellationToken ct)
    {
        Interlocked.Increment(ref _active);
        var threshold=ThresholdBytes;
        CancellationToken stop;lock(_stopGate)stop=_stop.Token;
        // The provider owns background I/O; never execute hashing or per-block callbacks on the UI dispatcher.
        return Task.Run(async()=>
        {
            using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct,stop);
            cancel.CancelAfter(TimeSpan.FromMinutes(30));
            var entered=false;
            try
            {
                long size;
                try { size=await TransferThreshold.ReadSizeAsync(request,cancel.Token).ConfigureAwait(false); }
                catch(Exception ex) when(ex is not OperationCanceledException) { throw new PluginTransferUnavailableException("大小预检未完成，使用普通 ADB 传输："+ex.Message,ex); }
                if(!TransferThreshold.ShouldUseHybrid(size,threshold))
                    throw new PluginTransferUnavailableException($"文件小于 {TransferThreshold.Describe(threshold)} 阈值，使用普通 ADB 传输。");
                await _serial.WaitAsync(cancel.Token).ConfigureAwait(false);entered=true;
                var session=await Ensure(request,cancel.Token).ConfigureAwait(false);
                await session.Transfer(request,progress,cancel.Token).ConfigureAwait(false);
                log("传输已通过 SHA-256 校验："+Path.GetFileName(request.LocalPath)+" · "+session.Channels);
            }
            catch(PluginTransferUnavailableException){throw;}
            catch
            {
                if(entered&&_context!=null){await _context.DisposeAsync().ConfigureAwait(false);_context=null;_key="";}
                throw;
            }
            finally{if(entered)_serial.Release();Interlocked.Decrement(ref _active);}
        },CancellationToken.None);
    }
    private async Task<HybridSession> Ensure(PluginTransferRequest request,CancellationToken ct)
    {
        var key=request.Serial+"|"+request.UseRoot+"|"+request.AdbPath+"|"+Wifi+"|"+MaxRails;
        if(_context?.Session!=null&&_context.IsAlive&&_key==key)return _context.Session;
        if(_context!=null){await _context.DisposeAsync().ConfigureAwait(false);_context=null;}
        try{_context=await AgentContext.Start(request,dataDirectory,Wifi,MaxRails,log,ct).ConfigureAwait(false);_key=key;return _context.Session!;}
        catch(Exception ex) when(ex is not OperationCanceledException){throw new PluginTransferUnavailableException(ex.Message,ex);}
    }
    public Task Probe(PluginTransferRequest request,CancellationToken ct)
    {
        Interlocked.Increment(ref _active);
        CancellationToken stop;lock(_stopGate)stop=_stop.Token;
        return Task.Run(async()=> { using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,stop);linked.CancelAfter(TimeSpan.FromSeconds(45));try{await TransferProbe(request,linked.Token);}finally{Interlocked.Decrement(ref _active);} },CancellationToken.None);
    }
    private async Task TransferProbe(PluginTransferRequest request,CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try{await Ensure(request,ct).ConfigureAwait(false);}finally{_serial.Release();}
    }
    public void Stop()
    {lock(_stopGate){var old=_stop;_stop=new();old.Cancel();old.Dispose();}}
    public async ValueTask DisposeAsync()
    {
        Stop();await _serial.WaitAsync().ConfigureAwait(false);
        try{if(_context!=null){await _context.DisposeAsync().ConfigureAwait(false);_context=null;}}
        finally{_serial.Release();_stop.Dispose();}
    }
}
