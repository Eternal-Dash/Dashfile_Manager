using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DashFile.Services;

/// <summary>ADB's documented sync v1 SEND/DATA/DONE protocol over the local server.
/// Bounded DATA packets expose byte progress without exec-in's hidden stdin buffering.</summary>
public static class AdbSyncUpload
{
    public static async Task PushAsync(AdbRunner adb,string serial,string local,string remote,Action<long> progress,CancellationToken ct,int? testPort=null)
    {
        if(string.IsNullOrWhiteSpace(serial)||serial.Any(char.IsControl))throw new IOException("未指定有效设备。");
        if(testPort==null)
        {
            var start=await adb.RunAsync(new[]{"start-server"},ct).ConfigureAwait(false);if(!start.Ok)throw new IOException(start.All);
        }
        var port=testPort??5037;
        if(testPort==null)
        {
            var socket=Environment.GetEnvironmentVariable("ADB_SERVER_SOCKET");
            if(!string.IsNullOrEmpty(socket))
            {
                var parts=socket.Split(':');
                if(parts.Length!=3||parts[0]!="tcp"||parts[1] is not ("localhost" or "127.0.0.1")||!int.TryParse(parts[2],out port))throw new IOException("当前 ADB_SERVER_SOCKET 不支持本机进度上传。");
            }
            else if(int.TryParse(Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_PORT"),out var configured))port=configured;
        }
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(adb.TransferTimeout);
        var token=deadline.Token;
        using var client=new TcpClient { NoDelay=true,SendBufferSize=65536 };
        using var cancel=token.Register(()=>client.Dispose());
        try
        {
            await client.ConnectAsync(IPAddress.Loopback,port,token).ConfigureAwait(false);var stream=client.GetStream();
            async Task Service(string command)
            {
                var bytes=Encoding.UTF8.GetBytes(command);if(bytes.Length>65535)throw new IOException("ADB 请求过长。");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(bytes.Length.ToString("x4",CultureInfo.InvariantCulture)),token).ConfigureAwait(false);
                await stream.WriteAsync(bytes,token).ConfigureAwait(false);
                var status=new byte[4];await stream.ReadExactlyAsync(status,token).ConfigureAwait(false);
                if(Encoding.ASCII.GetString(status)=="OKAY")return;
                if(Encoding.ASCII.GetString(status)!="FAIL")throw new IOException("ADB 服务响应无效。");
                await stream.ReadExactlyAsync(status,token).ConfigureAwait(false);var size=int.Parse(Encoding.ASCII.GetString(status),NumberStyles.HexNumber,CultureInfo.InvariantCulture);
                var error=new byte[size];await stream.ReadExactlyAsync(error,token).ConfigureAwait(false);throw new IOException(Encoding.UTF8.GetString(error));
            }
            await Service("host:transport:"+serial).ConfigureAwait(false);await Service("sync:").ConfigureAwait(false);
            var header=new byte[8];
            async Task Header(string id,uint length)
            {
                Encoding.ASCII.GetBytes(id,header);BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4),length);
                await stream.WriteAsync(header,token).ConfigureAwait(false);
            }
            var name=Encoding.UTF8.GetBytes(remote+",33152");if(name.Length>1024)throw new IOException("设备上传路径过长。");
            await Header("SEND",(uint)name.Length).ConfigureAwait(false);await stream.WriteAsync(name,token).ConfigureAwait(false);
            using var file=new FileStream(local,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true);
            var buffer=new byte[65536];long sent=0;int n;
            while((n=await file.ReadAsync(buffer,token).ConfigureAwait(false))>0)
            {
                await Header("DATA",(uint)n).ConfigureAwait(false);await stream.WriteAsync(buffer.AsMemory(0,n),token).ConfigureAwait(false);
                sent+=n;progress(Math.Min(sent,Math.Max(0,file.Length-1)));
            }
            var timestamp=(uint)Math.Clamp(new DateTimeOffset(File.GetLastWriteTimeUtc(local)).ToUnixTimeSeconds(),0,uint.MaxValue);
            await Header("DONE",timestamp).ConfigureAwait(false);await stream.ReadExactlyAsync(header,token).ConfigureAwait(false);
            var result=Encoding.ASCII.GetString(header,0,4);var count=BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if(result=="FAIL")
            {
                if(count>65536)throw new IOException("ADB 错误响应过大。");var error=new byte[count];await stream.ReadExactlyAsync(error,token).ConfigureAwait(false);throw new IOException(Encoding.UTF8.GetString(error));
            }
            if(result!="OKAY")throw new IOException("设备未确认上传完成。");progress(sent);
        }
        catch(Exception ex) when(token.IsCancellationRequested)
        {if(ct.IsCancellationRequested)throw new OperationCanceledException("上传已取消",ex,ct);throw new TimeoutException("ADB 上传超时。",ex);}
    }
}
