// HybridFileXfer multi-I/O block scheduling adaptation. Original author: weixiansen574.
// GPL-3.0-or-later. See ThirdParty/HybridFileXfer-LICENSE.txt and UPSTREAM.md.
using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using DashFile.PluginApi;

namespace HybridTransferPlugin;

public sealed class HybridSession : IAsyncDisposable
{
    public const int BlockSize = 1024*1024;
    public const long MaximumFile = 1L << 40;
    private readonly HybridChannel _control;
    private readonly List<HybridChannel> _rails;
    private readonly SemaphoreSlim _gate = new(1,1);
    private bool _disposed;
    public string Serial { get; }
    public string Uid { get; }
    public string? PendingUploadPath { get; private set; }
    public string Channels => _rails.Count == 1 ? "USB 单通道" : string.Join(" + ", _rails.Select(r => r.Name));
    public IReadOnlyDictionary<string,long> Traffic => _rails.ToDictionary(r=>r.Name,r=>Interlocked.Read(ref r.Bytes));
    private HybridSession(HybridChannel control,List<HybridChannel> rails,string serial,string uid) { _control=control;_rails=rails;Serial=serial;Uid=uid; }
    public static CancellationTokenSource Deadline(CancellationToken ct, double seconds=45)
    { var c=CancellationTokenSource.CreateLinkedTokenSource(ct);c.CancelAfter(TimeSpan.FromSeconds(seconds));return c; }
    public static bool PrivateAddress(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) return false;
        var b=ip.GetAddressBytes();return b[0]==10 || b[0]==192&&b[1]==168 || b[0]==172&&b[1]>=16&&b[1]<=31 || b[0]==169&&b[1]==254;
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification="Factory transfers ownership on success and disposes all acquired resources in catch; deadline sources use using declarations.")]
    public static async Task<HybridSession> Connect(int forwardedPort,int devicePort,string serial,bool root,string token,byte[] certificate,
        bool wifi,int maxRails,Action<string>? log,CancellationToken ct,bool loopbackTest=false)
    {
        HybridChannel? control=null;var rails=new List<HybridChannel>();
        try
        {
            using var hello=Deadline(ct,10);
            control=await HybridChannel.Open(new("控制",IPAddress.Loopback,forwardedPort),token,certificate,hello.Token);
            await control.Wire.WriteByte(1,hello.Token);await control.Wire.Reply(hello.Token);
            var actual=await control.Wire.ReadUtf(hello.Token);var uid=await control.Wire.ReadUtf(hello.Token);
            if(actual!=serial || root&&uid!="0")throw new IOException("传输服务设备身份或 Root 身份不匹配。");
            if(await control.Wire.ReadInt(hello.Token)!=BlockSize)throw new IOException("分块协议不匹配。");
            var count=await control.Wire.ReadInt(hello.Token);if(count<0||count>8)throw new IOException("通道数量无效。");
            var endpoints=new List<HybridEndpoint> {new("USB",IPAddress.Loopback,forwardedPort)};
            for(var i=0;i<count;i++)
            {
                var name=await control.Wire.ReadUtf(hello.Token);var ip=await control.Wire.ReadUtf(hello.Token);
                if(wifi&&IPAddress.TryParse(ip,out var address)&&(PrivateAddress(address)||loopbackTest&&IPAddress.IsLoopback(address)))
                    endpoints.Add(new(name,address,devicePort));
            }
            foreach(var endpoint in endpoints.Take(Math.Clamp(maxRails,1,4)))
            {
                try { using var timeout=Deadline(ct,endpoint.Name=="USB"?8:1.5);rails.Add(await HybridChannel.Open(endpoint,token,certificate,timeout.Token)); }
                catch(Exception ex) when (endpoint.Name!="USB"&&!ct.IsCancellationRequested) { log?.Invoke(endpoint.Name+"不可达，跳过该通道："+ex.GetType().Name); }
            }
            if(rails.Count==0)throw new IOException("USB 传输通道未连接。");
            return new HybridSession(control,rails,serial,uid);
        }
        catch { control?.Dispose();foreach(var rail in rails)rail.Dispose();throw; }
    }
    public async Task Transfer(PluginTransferRequest request,IProgress<PluginTransferProgress>? progress,CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(request.Serial!=Serial || request.UseRoot&&Uid!="0")throw new IOException("设备身份与传输会话不匹配。");
            if(request.Direction==PluginTransferDirection.Download)await Download(request,progress,ct);else await Upload(request,progress,ct);
        }
        finally { _gate.Release(); }
    }
    public async Task CheckAlive(CancellationToken ct)
    {
        using var limit=Deadline(ct,3);var w=_control.Wire;
        await w.WriteByte(1,limit.Token);await w.Reply(limit.Token);
        if(await w.ReadUtf(limit.Token)!=Serial||await w.ReadUtf(limit.Token)!=Uid||await w.ReadInt(limit.Token)!=BlockSize)throw new IOException("传输服务身份已变化。");
        var count=await w.ReadInt(limit.Token);if(count<0||count>8)throw new IOException("传输服务响应无效。");
        for(var i=0;i<count;i++){await w.ReadUtf(limit.Token);await w.ReadUtf(limit.Token);}
    }
    private async Task<(long Length,long Modified)> Stat(string path,CancellationToken ct)
    {
        using var limit=Deadline(ct);var w=_control.Wire;
        await w.WriteByte(2,limit.Token);await w.WriteUtf(path,limit.Token);await w.Reply(limit.Token);
        var length=await w.ReadLong(limit.Token);var modified=await w.ReadLong(limit.Token);
        if(length<0||length>MaximumFile)throw new IOException("文件长度超出支持范围。");return(length,modified);
    }
    private async Task<string> RemoteHash(string path,long length,long modified,CancellationToken ct)
    {
        using var limit=Deadline(ct,Math.Clamp(length/(16d*1024*1024)+60,60,1800));var w=_control.Wire;
        await w.WriteByte(8,limit.Token);await w.WriteUtf(path,limit.Token);await w.WriteLong(length,limit.Token);await w.WriteLong(modified,limit.Token);await w.Reply(limit.Token);
        var sha=await w.ReadUtf(limit.Token);if(sha.Length!=64||!sha.All(Uri.IsHexDigit))throw new IOException("SHA-256 响应无效。");return sha;
    }
    private async Task Download(PluginTransferRequest r,IProgress<PluginTransferProgress>? progress,CancellationToken ct)
    {
        var info=await Stat(r.RemotePath,ct);var destination=Path.GetFullPath(r.LocalPath);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial=destination+".dashfile-hfx-"+Guid.NewGuid().ToString("N")+".partial";
        try
        {
            using(var file=new FileStream(partial,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read,BlockSize,FileOptions.Asynchronous|FileOptions.RandomAccess))
            {
                file.SetLength(info.Length);
                await Blocks(info.Length,async(rail,offset,bytes,token)=>
                {
                    var w=rail.Wire;await w.WriteByte(3,token);await w.WriteUtf(r.RemotePath,token);await w.WriteLong(offset,token);await w.WriteInt(bytes.Length,token);
                    await w.WriteLong(info.Length,token);await w.WriteLong(info.Modified,token);await w.Reply(token);
                    if(await w.ReadInt(token)!=bytes.Length)throw new IOException("回传分块长度不符。");
                    await rail.Stream.ReadExactlyAsync(bytes,token);await RandomAccess.WriteAsync(file.SafeFileHandle,bytes,offset,token);
                },r.LocalPath,progress,ct);
                progress?.Report(new(info.Length,info.Length,Channels,Path.GetFileName(r.LocalPath),"校验中"));
                await file.FlushAsync(ct);file.Position=0;
                var local=Convert.ToHexString(await SHA256.HashDataAsync(file,ct));var remote=await RemoteHash(r.RemotePath,info.Length,info.Modified,ct);
                if(!local.Equals(remote,StringComparison.OrdinalIgnoreCase))throw new IOException("下载 SHA-256 校验失败，未替换目标文件。");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(partial,destination,true);
            progress?.Report(new(info.Length,info.Length,Channels,Path.GetFileName(r.LocalPath),"已校验"));
            if(info.Modified>0)try{File.SetLastWriteTimeUtc(destination,DateTimeOffset.FromUnixTimeMilliseconds(info.Modified).UtcDateTime);}catch(ArgumentOutOfRangeException){}
        }
        finally {if(File.Exists(partial))File.Delete(partial);}
    }
    private async Task Upload(PluginTransferRequest r,IProgress<PluginTransferProgress>? progress,CancellationToken ct)
    {
        using var file=new FileStream(r.LocalPath,FileMode.Open,FileAccess.Read,FileShare.Read,BlockSize,FileOptions.Asynchronous|FileOptions.RandomAccess);
        var length=file.Length;if(length>MaximumFile)throw new IOException("文件超出支持范围。");
        var modified=new DateTimeOffset(File.GetLastWriteTimeUtc(r.LocalPath)).ToUnixTimeMilliseconds();
        progress?.Report(new(0,length,Channels,Path.GetFileName(r.LocalPath),"计算校验值"));
        var sha=Convert.ToHexString(await SHA256.HashDataAsync(file,ct));string? id=null;
        try
        {
            ct.ThrowIfCancellationRequested();
            id=Guid.NewGuid().ToString("N");
            var slash=r.RemotePath.LastIndexOf('/');
            PendingUploadPath=(slash>=0?r.RemotePath[..(slash+1)]:Path.GetDirectoryName(r.RemotePath)+Path.DirectorySeparatorChar)+".dashfile-hfx-"+id+".partial";
            // Finish this small setup RPC even if cancellation arrives, so its owned path
            // is known and cleanup cannot race an incomplete BEGIN frame.
            using(var limit=Deadline(CancellationToken.None,8))
            {
                var w=_control.Wire;await w.WriteByte(4,limit.Token);await w.WriteUtf(id,limit.Token);await w.WriteUtf(r.RemotePath,limit.Token);await w.WriteLong(length,limit.Token);await w.WriteLong(modified,limit.Token);await w.Reply(limit.Token);var confirmed=await w.ReadUtf(limit.Token);
                if(confirmed!=id||id.Length!=32||!id.All(Uri.IsHexDigit))throw new IOException("上传标识无效。");
                var pending=await w.ReadUtf(limit.Token);
                if(!pending.EndsWith(".dashfile-hfx-"+id+".partial",StringComparison.Ordinal)||pending.Contains('\0'))throw new IOException("上传临时路径无效。");
                PendingUploadPath=pending;
            }
            await Blocks(length,async(rail,offset,bytes,token)=>
            {
                var read = 0;
                while (read < bytes.Length) { var n = await RandomAccess.ReadAsync(file.SafeFileHandle, bytes[read..], offset + read, token); if (n == 0) throw new IOException("本地文件读取不完整。"); read += n; }
                var w=rail.Wire;await w.WriteByte(5,token);await w.WriteUtf(id,token);await w.WriteLong(offset,token);await w.WriteInt(bytes.Length,token);await rail.Stream.WriteAsync(bytes,token);await w.Reply(token);
            },r.LocalPath,progress,ct);
            progress?.Report(new(length,length,Channels,Path.GetFileName(r.LocalPath),"校验并提交"));
            using(var limit=Deadline(ct,Math.Clamp(length/(16d*1024*1024)+60,60,1800)))
            { var w=_control.Wire;await w.WriteByte(6,limit.Token);await w.WriteUtf(id,limit.Token);await w.WriteUtf(sha,limit.Token);await w.Reply(limit.Token);id=null;PendingUploadPath=null; }
            progress?.Report(new(length,length,Channels,Path.GetFileName(r.LocalPath),"已校验"));
        }
        finally
        {
            if(id!=null)try{using var cleanup=Deadline(CancellationToken.None,2);await _control.Wire.WriteByte(7,cleanup.Token);await _control.Wire.WriteUtf(id,cleanup.Token);await _control.Wire.Reply(cleanup.Token);}catch{}
        }
    }
    private async Task Blocks(long length,Func<HybridChannel,long,Memory<byte>,CancellationToken,Task> block,string file,IProgress<PluginTransferProgress>? progress,CancellationToken ct)
    {
        long next=-1,completed=0;using var all=CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Worker(HybridChannel rail)
        {
            var buffer=ArrayPool<byte>.Shared.Rent(BlockSize);
            try
            {
                while(true)
                {
                    var offset=Interlocked.Increment(ref next)*BlockSize;if(offset>=length)break;
                    var count=(int)Math.Min(BlockSize,length-offset);using var timeout=Deadline(all.Token);
                    await block(rail,offset,buffer.AsMemory(0,count),timeout.Token);
                    Interlocked.Add(ref rail.Bytes,count);var done=Interlocked.Add(ref completed,count);progress?.Report(new(done,length,Channels,Path.GetFileName(file)));
                }
            }
            catch {all.Cancel();throw;}
            finally {ArrayPool<byte>.Shared.Return(buffer);}
        }
        await Task.WhenAll(_rails.Select(Worker));
        if(completed!=length)throw new IOException("传输分块数量不完整。");
        progress?.Report(new(length,length,Channels,Path.GetFileName(file)));
    }
    public async ValueTask DisposeAsync()
    {
        if(_disposed)return;_disposed=true;
        try{using var limit=Deadline(CancellationToken.None,1);await _control.Wire.WriteByte(9,limit.Token);await _control.Wire.Reply(limit.Token);}catch{}
        foreach(var r in _rails)r.Dispose();_control.Dispose();
    }
}
