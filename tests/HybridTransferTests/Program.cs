using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DashFile.PluginApi;
using DashFile.Services;
using HybridTransferPlugin;

// Child mode models ADB binary stdout and failures without touching any device.
if(args.Length>1 && args[0]=="-s" && args[1]=="MOCK")
{
    var mode=args[^1];
    if(mode.Contains("stat -L",StringComparison.Ordinal))
    {
        if(mode.Contains("/invalid-size",StringComparison.Ordinal)){Console.Write("unknown");return 0;}
        Console.Write(mode.Contains("/one-gb",StringComparison.Ordinal)?"1073741824":mode.Contains("/empty",StringComparison.Ordinal)?"0":"256");return 0;
    }
    if(mode=="/sleep"){await Task.Delay(30000);return 0;}
    if(mode.Contains("rm -f",StringComparison.Ordinal))return 0;
    if(args.Contains("exec-in")){using var stdin=Console.OpenStandardInput();await stdin.CopyToAsync(Stream.Null);return 0;}
    using var output=Console.OpenStandardOutput();
    if(mode!="/empty")await output.WriteAsync(Enumerable.Range(0,256).Select(x=>(byte)x).ToArray());
    await output.FlushAsync();
    if(mode=="/partial-fail"){Console.Error.Write("permission denied after partial bytes");return 7;}
    return 0;
}
if(args.Length==3 && args[0]=="--device-test")
{
    // Explicit opt-in only: create/delete generated ordinary files, never raw partitions/reboot.
    var fixture=Path.Combine(Path.GetTempPath(),"DashFile-HybridDevice-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
    var recoveredIdle=false;
    await using var provider=new HybridProvider(fixture,message=>{Console.WriteLine(message);if(message.Contains("闲置连接已失效"))recoveredIdle=true;}){ThresholdBytes=TransferThreshold.Megabyte};var deviceRegistry=new FileTransferRegistry();using var registration=deviceRegistry.Register("device-test",provider,100);
    var fallback=false;deviceRegistry.Status+=message=>{Console.WriteLine(message);if(message.Contains("使用 ADB"))fallback=true;};
    var runner=new AdbRunner(args[1]);using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var source=Path.Combine(fixture,".dashfile-hfx-test-"+Guid.NewGuid().ToString("N")+" 中文.bin");var bytes=new byte[16*1024*1024+173];RandomNumberGenerator.Fill(bytes);await File.WriteAllBytesAsync(source,bytes);
    await provider.Probe(new(args[2],false,args[1],PluginTransferDirection.Download,"/sdcard/",""),deadline.Token);
    var agent=(AgentContext?)typeof(HybridProvider).GetField("_context",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(provider);
    var closedControl=(HybridChannel)typeof(HybridSession).GetField("_control",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(agent!.Session)!;
    closedControl.Dispose(); // Only the fixture-owned connection: simulate stale transport without waiting 3 minutes.
    foreach(var useRoot in new[]{false,true})
    {
        var fs=new RemoteFileSystem(runner,args[2],useRoot,deviceRegistry);var remoteDir=useRoot?DevicePaths.LocalTmp:"/sdcard/Download";var remote=Sh.Join(remoteDir,Path.GetFileName(source));
        try
        {
            provider.ThresholdBytes=TransferThreshold.DefaultBytes;fallback=false;
            var trafficBefore=provider.Traffic.Values.Sum();
            await fs.UploadAsync(source,remoteDir,deadline.Token);
            var smallDownload=Path.Combine(fixture,useRoot?"root-small.bin":"shell-small.bin");
            await fs.DownloadAsync(remote,smallDownload,deadline.Token);
            if(!fallback||provider.Traffic.Values.Sum()!=trafficBefore||!SHA256.HashData(await File.ReadAllBytesAsync(smallDownload)).SequenceEqual(SHA256.HashData(bytes)))throw new IOException("Small-file ADB bypass failed");
            Console.WriteLine($"PASS physical {(useRoot?"root":"shell")} below 1 GB uploads/downloads via ordinary ADB, no hybrid data");
            provider.ThresholdBytes=TransferThreshold.Megabyte;fallback=false;
            var watch=Stopwatch.StartNew();await fs.UploadAsync(source,remoteDir,deadline.Token);var upload=watch.Elapsed.TotalSeconds;
            var downloaded=Path.Combine(fixture,useRoot?"root-download.bin":"shell-download.bin");watch.Restart();await fs.DownloadAsync(remote,downloaded,deadline.Token);var download=watch.Elapsed.TotalSeconds;
            if(!SHA256.HashData(await File.ReadAllBytesAsync(downloaded)).SequenceEqual(SHA256.HashData(bytes))||fallback)throw new IOException("Device verification failed or silently fell back");
            Console.WriteLine($"PASS physical {(useRoot?"root":"shell")} routed upload/download SHA-256 · {provider.ChannelSummary} · upload {upload:F2}s download {download:F2}s");
            foreach(var rail in provider.Traffic)Console.WriteLine($"TRAFFIC {rail.Key}: {rail.Value} bytes");
            if(provider.Traffic.Count>1 && provider.Traffic.Values.Any(value=>value==0))throw new IOException("Connected rail carried no data");
        }
        finally { await fs.DeleteAsync(new[]{remote},CancellationToken.None); }
    }
    if(!recoveredIdle)throw new IOException("Stale connection was not rebuilt before file transfer");
    Console.WriteLine("PASS physical stale control socket is rebuilt before accepting file data");
    Console.WriteLine("PHYSICAL PASS: only generated test files used and removed; no partition reads/writes/reboots.");return 0;
}
var passed=0;var failed=0;
void Check(string name,bool ok){Console.WriteLine((ok?"PASS ":"FAIL ")+name);if(ok)passed++;else failed++;}
async Task Reject(string name,Func<Task> action){try{await action();Check(name,false);}catch{Check(name,true);}}
var root=Path.Combine(Path.GetTempPath(),"DashFile-HybridTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
var ct=CancellationToken.None;
foreach(var text in new[]{"", "ascii", "中文 路径 / x' &.txt", "nul\0test", "😀文件"})Check("Java modified UTF roundtrip "+text.Replace('\0','?'),HybridWire.DecodeUtf(HybridWire.EncodeUtf(text))==text);
await Reject("reject malformed UTF continuation",()=>Task.FromResult(HybridWire.DecodeUtf(new byte[]{0xc0,0x12})));
Check("private WiFi address policy",HybridSession.PrivateAddress(IPAddress.Parse("192.168.1.5"))&&!HybridSession.PrivateAddress(IPAddress.Parse("8.8.8.8"))&&!HybridSession.PrivateAddress(IPAddress.Loopback));
var registry=new FileTransferRegistry();var fake=new FakeProvider();using(var lease=registry.Register("fake",fake,100))
{
 var request=new PluginTransferRequest("TEST",false,"none",PluginTransferDirection.Download,"/sdcard/x",Path.Combine(root,"x"));
 Check("registered provider is preferred",await registry.TryTransferAsync(request,ct)&&fake.Calls==1);
 fake.Unavailable=true;Check("only preflight unavailable allows fallback",!await registry.TryTransferAsync(request,ct));fake.Unavailable=false;
 fake.Fail=true;await Reject("mid-transfer error is never silently retried with ADB",()=>registry.TryTransferAsync(request,ct));fake.Fail=false;
 fake.Enabled=false;Check("disabled provider restores host backend",!await registry.TryTransferAsync(request,ct));
}
Check("unregistered provider is not retained",!await registry.TryTransferAsync(new("TEST",false,"none",PluginTransferDirection.Download,"/a",Path.Combine(root,"a")),ct));
var adbRunner=new AdbRunner(Environment.ProcessPath!){TransferTimeout=TimeSpan.FromSeconds(5)};
var remoteFs=new RemoteFileSystem(adbRunner,"MOCK",false);
var nativeTarget=Path.Combine(root,"native.bin");
await remoteFs.DownloadAsync("/success",nativeTarget,ct);
Check("host binary fallback preserves all 256 byte values",(await File.ReadAllBytesAsync(nativeTarget)).SequenceEqual(Enumerable.Range(0,256).Select(x=>(byte)x)));
await File.WriteAllTextAsync(nativeTarget,"keep original");
await Reject("nonzero exit with nonempty bytes rejects download",()=>remoteFs.DownloadAsync("/partial-fail",nativeTarget,ct));
Check("partial ADB failure preserves old destination",await File.ReadAllTextAsync(nativeTarget)=="keep original");
await remoteFs.DownloadAsync("/empty",nativeTarget,ct);Check("empty successful ADB file is valid",new FileInfo(nativeTarget).Length==0);
await File.WriteAllTextAsync(nativeTarget,"keep on timeout");adbRunner.TransferTimeout=TimeSpan.FromMilliseconds(180);
await Reject("streaming ADB timeout terminates child",()=>remoteFs.DownloadAsync("/sleep",nativeTarget,ct));
Check("timeout preserves destination and cleans partial files",await File.ReadAllTextAsync(nativeTarget)=="keep on timeout"&&!Directory.GetFiles(root,"*.partial").Any());
adbRunner.TransferTimeout=TimeSpan.FromSeconds(5);using(var cancel=new CancellationTokenSource(180))await Reject("streaming ADB cancellation",()=>remoteFs.DownloadAsync("/sleep",nativeTarget,cancel.Token));
Check("cancel preserves destination and cleans partial files",await File.ReadAllTextAsync(nativeTarget)=="keep on timeout"&&!Directory.GetFiles(root,"*.partial").Any());
var syncSource=Path.Combine(root,"sync-upload.bin");var syncPayload=new byte[3*1024*1024+53];RandomNumberGenerator.Fill(syncPayload);await File.WriteAllBytesAsync(syncSource,syncPayload);
await using(var fixture=new SyncFixture(false))
{
    long acknowledged=0;await AdbSyncUpload.PushAsync(adbRunner,"MOCK",syncSource,"/data/local/tmp/owned-test",n=>acknowledged=n,ct,fixture.Port);await fixture.Completed;
    Check("ADB sync upload frames preserve exact binary contents",fixture.Data.SequenceEqual(syncPayload)&&fixture.Target=="/data/local/tmp/owned-test,33152");
    Check("ADB sync packets are bounded and completion acknowledged",fixture.MaxPacket<=65536&&acknowledged==syncPayload.Length);
}
await using(var fixture=new SyncFixture(true))
{
    long acknowledged=0;await Reject("ADB sync rejects device failure ACK",()=>AdbSyncUpload.PushAsync(adbRunner,"MOCK",syncSource,"/data/local/tmp/owned-test",n=>acknowledged=n,ct,fixture.Port));
    Check("ADB sync never reports full success before peer ACK",acknowledged<syncPayload.Length);
}
await using(var fixture=new SyncFixture(false))
{
    using var stopSync=new CancellationTokenSource();await Reject("ADB sync cancellation closes only its own socket",()=>AdbSyncUpload.PushAsync(adbRunner,"MOCK",syncSource,"/data/local/tmp/owned-test",n=>{if(n>=65536)stopSync.Cancel();},stopSync.Token,fixture.Port));
}
var repo=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
Check("default threshold is 1 GB",TransferThreshold.DefaultBytes==1073741824);
Check("1 byte below threshold uses ADB",!TransferThreshold.ShouldUseHybrid(TransferThreshold.DefaultBytes-1,TransferThreshold.DefaultBytes));
Check("exact threshold enables hybrid",TransferThreshold.ShouldUseHybrid(TransferThreshold.DefaultBytes,TransferThreshold.DefaultBytes));
Check("above threshold enables hybrid",TransferThreshold.ShouldUseHybrid(TransferThreshold.DefaultBytes+1,TransferThreshold.DefaultBytes));
Check("empty file stays on ADB",!TransferThreshold.ShouldUseHybrid(0,TransferThreshold.DefaultBytes));
Check("MB refinement exact conversion",TransferThreshold.FromDisplay(1.0009765625m,true)==1025*TransferThreshold.Megabyte);
Check("unit switch preserves bytes",TransferThreshold.FromDisplay(1025,false)==TransferThreshold.FromDisplay(1.0009765625m,true));
Check("threshold is bounded to 1 MB precision",TransferThreshold.FromDisplay(0,false)==TransferThreshold.Megabyte&&TransferThreshold.Normalize(-1)==TransferThreshold.DefaultBytes);
await using(var thresholdProvider=new HybridProvider(root,Console.WriteLine))
{
    Check("provider migration defaults to 1 GB",thresholdProvider.ThresholdBytes==TransferThreshold.DefaultBytes);
    var small=Path.Combine(root,"small-upload.bin");await File.WriteAllBytesAsync(small,new byte[256]);
    using var thresholdLease=registry.Register("threshold",thresholdProvider,100);
    Check("small upload bypasses before agent startup",!await registry.TryTransferAsync(new("MOCK",false,Environment.ProcessPath!,PluginTransferDirection.Upload,"/small-upload",small),ct)&&thresholdProvider.ChannelSummary=="尚未连接");
    Check("remote stat is read through explicit device",await TransferThreshold.ReadSizeAsync(new("MOCK",false,Environment.ProcessPath!,PluginTransferDirection.Download,"/one-gb",nativeTarget),ct)==TransferThreshold.DefaultBytes);
    Check("root remote stat uses same size rules",await TransferThreshold.ReadSizeAsync(new("MOCK",true,Environment.ProcessPath!,PluginTransferDirection.Download,"/one-gb",nativeTarget),ct)==TransferThreshold.DefaultBytes);
    Check("small download bypasses without agent",!await registry.TryTransferAsync(new("MOCK",false,Environment.ProcessPath!,PluginTransferDirection.Download,"/small-download",nativeTarget),ct)&&thresholdProvider.ChannelSummary=="尚未连接");
    Check("invalid remote size safely falls back before writes",!await registry.TryTransferAsync(new("MOCK",false,Environment.ProcessPath!,PluginTransferDirection.Download,"/invalid-size",nativeTarget),ct));
    using var cancelled=new CancellationTokenSource();cancelled.Cancel();
    await Reject("size preflight respects cancellation",()=>TransferThreshold.ReadSizeAsync(new("MOCK",false,Environment.ProcessPath!,PluginTransferDirection.Download,"/one-gb",nativeTarget),cancelled.Token));
    Check("size skip releases operation guard",!thresholdProvider.Active&&registry.ActiveCount==0);
}
var java=Environment.GetEnvironmentVariable("HYBRID_TEST_JAVA")??Path.Combine(Environment.GetEnvironmentVariable("JAVA_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"dev","jdk-21"),"bin","java.exe");
var classes=Environment.GetEnvironmentVariable("HYBRID_TEST_CLASSES")??Path.Combine(repo,"artifacts","hybrid-agent","classes");
if(!Directory.Exists(classes))throw new IOException("Run build/build-hybrid-agent.ps1 before JVM integration tests.");
using var rsa=RSA.Create(2048);var requestCert=new CertificateRequest("CN=dashfile-hybrid.local",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
using var cert=requestCert.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));
var password="Test-"+Guid.NewGuid().ToString("N");var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();var p12=Path.Combine(root,"test.p12");await File.WriteAllBytesAsync(p12,cert.Export(X509ContentType.Pkcs12,password));
using var process=new Process{StartInfo=new ProcessStartInfo(java){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}};
foreach(var arg in new[]{"-cp",classes,"com.dashfile.hybrid.Agent",p12,password,token,"JVM-TEST","127.0.0.1","test"})process.StartInfo.ArgumentList.Add(arg);
process.Start();var errors=process.StandardError.ReadToEndAsync();
try
{
 var ready=await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
 if(ready==null||!ready.StartsWith("DHFX_READY "))throw new Exception("JVM peer did not start");
 var port=int.Parse(ready.Split(' ')[1]);var pin=SHA256.HashData(cert.RawData);
 await Reject("TLS rejects a different certificate",()=>HybridSession.Connect(port,port,"JVM-TEST",false,token,new byte[32],true,3,null,ct,true));
 await Reject("authentication rejects wrong session token",()=>HybridSession.Connect(port,port,"JVM-TEST",false,new string('a',64),pin,true,3,null,ct,true));
 await Reject("session rejects wrong device serial",()=>HybridSession.Connect(port,port,"OTHER",false,token,pin,true,3,null,ct,true));
 await using var session=await HybridSession.Connect(port,port,"JVM-TEST",false,token,pin,true,3,Console.WriteLine,ct,true);
 Check("two simulated rails connect",session.Traffic.Count==2);
 await session.CheckAlive(ct);Check("cached connection preflight verifies live peer",true);
 var bytes=new byte[12*1024*1024+137];RandomNumberGenerator.Fill(bytes);var source=Path.Combine(root,"原始 😀 & 文件.bin");await File.WriteAllBytesAsync(source,bytes);
 var dest=Path.Combine(root,"下载.bin");await File.WriteAllTextAsync(dest,"existing destination");
 await session.Transfer(new("JVM-TEST",false,"none",PluginTransferDirection.Download,source,dest),null,ct);
 Check("multi-rail download exact bytes and hash",SHA256.HashData(await File.ReadAllBytesAsync(dest)).SequenceEqual(SHA256.HashData(bytes)));
 Check("both simulated rails carry data",session.Traffic.Values.All(n=>n>0));
 var remote=Path.Combine(root,"upload 😀 target.bin");await File.WriteAllTextAsync(remote,"old");
 await session.Transfer(new("JVM-TEST",false,"none",PluginTransferDirection.Upload,remote,source),null,ct);
 Check("upload commits verified exact bytes",SHA256.HashData(await File.ReadAllBytesAsync(remote)).SequenceEqual(SHA256.HashData(bytes)));
 var empty=Path.Combine(root,"empty");await File.WriteAllBytesAsync(empty,Array.Empty<byte>());var emptyDest=Path.Combine(root,"empty-copy");
 await session.Transfer(new("JVM-TEST",false,"none",PluginTransferDirection.Download,empty,emptyDest),null,ct);Check("zero-byte file is valid",new FileInfo(emptyDest).Length==0);
 var preserved=Path.Combine(root,"preserved");await File.WriteAllTextAsync(preserved,"keep me");
 await Reject("missing source fails",()=>session.Transfer(new("JVM-TEST",false,"none",PluginTransferDirection.Download,Path.Combine(root,"missing"),preserved),null,ct));
 Check("failed download preserves old destination",await File.ReadAllTextAsync(preserved)=="keep me");
 await Reject("session cannot be reused for another serial",()=>session.Transfer(new("OTHER",false,"none",PluginTransferDirection.Download,source,dest),null,ct));
 // Protocol-level failure tests: no phone involved, targets remain local JVM fixtures.
 using(var channel=await HybridChannel.Open(new("test",IPAddress.Loopback,port),token,pin,ct))
 {
     var w=channel.Wire;var protectedTarget=Path.Combine(root,"bad-hash-target");await File.WriteAllTextAsync(protectedTarget,"keep original");
     await w.WriteByte(4,ct);await w.WriteUtf(Guid.NewGuid().ToString("N"),ct);await w.WriteUtf(protectedTarget,ct);await w.WriteLong(0,ct);await w.WriteLong(0,ct);await w.Reply(ct);var id=await w.ReadUtf(ct);_ = await w.ReadUtf(ct);
     await w.WriteByte(6,ct);await w.WriteUtf(id,ct);await w.WriteUtf(new string('0',64),ct);
     await Reject("device rejects SHA-256 mismatch before commit",()=>w.Reply(ct));
     Check("bad hash preserves existing upload destination",await File.ReadAllTextAsync(protectedTarget)=="keep original");
     await w.WriteByte(7,ct);await w.WriteUtf(id,ct);await w.Reply(ct);
     Check("abort removes only owned upload partial",!Directory.GetFiles(root,".dashfile-hfx-*.partial").Any());
 }
 using(var cancel=new CancellationTokenSource())
 {
     await File.WriteAllTextAsync(preserved,"keep cancelled");
     await Reject("download cancellation after first block",()=>session.Transfer(new("JVM-TEST",false,"none",PluginTransferDirection.Download,source,preserved),new CancelProgress(cancel),cancel.Token));
     Check("cancelled multi-rail download preserves destination",await File.ReadAllTextAsync(preserved)=="keep cancelled"&&!Directory.GetFiles(root,"*.partial").Any());
 }
 await using(var usb=await HybridSession.Connect(port,port,"JVM-TEST",false,token,pin,false,3,null,ct,true))
 {
   Check("WiFi disabled uses a truthful single-rail label",usb.Channels=="USB 单通道");
   await using var uploadCancelSession=await HybridSession.Connect(port,port,"JVM-TEST",false,token,pin,true,3,null,ct,true);
   var cancelTarget=Path.Combine(root,"cancel-upload-target");await File.WriteAllTextAsync(cancelTarget,"keep upload original");
   using var stopUpload=new CancellationTokenSource();
   await Reject("cancel multi-rail upload with writes in flight",()=>uploadCancelSession.Transfer(new("JVM-TEST",false,"none",PluginTransferDirection.Upload,cancelTarget,source),new CancelProgress(stopUpload),stopUpload.Token));
   Check("upload cancellation preserves previous destination",await File.ReadAllTextAsync(cancelTarget)=="keep upload original");
   Check("aborted data rails cannot recreate a deleted partial",!Directory.GetFiles(root,".dashfile-hfx-*.partial").Any());
 }
}
finally
{
 try{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}catch{}
 var text=await errors;if(text.Length>0)Console.WriteLine("JVM diagnostics: "+text[..Math.Min(text.Length,1500)]);
}
Console.WriteLine($"{passed} passed, {failed} failed; local loopback/JVM only. Fixtures: {root}");return failed==0?0:1;

sealed class FakeProvider:IPluginFileTransferProvider
{
 public bool Unavailable,Fail;public int Calls;public bool Enabled=true;
 public string DisplayName=>"fake";public bool IsEnabled=>Enabled;
 public Task TransferAsync(PluginTransferRequest request,IProgress<PluginTransferProgress>? progress,CancellationToken ct)
 {Calls++;if(Unavailable)throw new PluginTransferUnavailableException("before data");if(Fail)throw new IOException("after accepting");return Task.CompletedTask;}
}

sealed class CancelProgress(CancellationTokenSource cancel):IProgress<PluginTransferProgress>
{public void Report(PluginTransferProgress p){if(p.CompletedBytes>0)cancel.Cancel();}}

sealed class SyncFixture:IAsyncDisposable
{
 private readonly System.Net.Sockets.TcpListener _listener=new(IPAddress.Loopback,0);
 private readonly CancellationTokenSource _stop=new();private readonly MemoryStream _data=new();
 public int Port {get;} public Task Completed {get;} public byte[] Data=>_data.ToArray(); public string Target="";public int MaxPacket;
 public SyncFixture(bool fail){_listener.Start();Port=((IPEndPoint)_listener.LocalEndpoint).Port;Completed=Serve(fail);}
 private async Task Serve(bool fail)
 {
  try
  {
   using var connection=await _listener.AcceptTcpClientAsync(_stop.Token);var stream=connection.GetStream();var header=new byte[8];
   for(int i=0;i<2;i++)
   {
    await stream.ReadExactlyAsync(header.AsMemory(0,4),_stop.Token);var size=int.Parse(System.Text.Encoding.ASCII.GetString(header,0,4),System.Globalization.NumberStyles.HexNumber);
    var command=new byte[size];await stream.ReadExactlyAsync(command,_stop.Token);
    if(System.Text.Encoding.UTF8.GetString(command)!=(i==0?"host:transport:MOCK":"sync:"))throw new IOException("wrong explicit device service");
    await stream.WriteAsync("OKAY"u8.ToArray(),_stop.Token);
   }
   while(true)
   {
    await stream.ReadExactlyAsync(header,_stop.Token);var id=System.Text.Encoding.ASCII.GetString(header,0,4);var count=System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
    if(id=="DONE")
    {
     if(fail){var message="device rejected upload"u8.ToArray();"FAIL"u8.CopyTo(header);System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4),(uint)message.Length);await stream.WriteAsync(header,_stop.Token);await stream.WriteAsync(message,_stop.Token);}
     else {"OKAY"u8.CopyTo(header);System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4),0);await stream.WriteAsync(header,_stop.Token);}break;
    }
    if(count>65536)throw new IOException("oversized sync packet");var payload=new byte[count];await stream.ReadExactlyAsync(payload,_stop.Token);
    if(id=="SEND")Target=System.Text.Encoding.UTF8.GetString(payload);
    else if(id=="DATA"){MaxPacket=Math.Max(MaxPacket,(int)count);await _data.WriteAsync(payload,_stop.Token);}else throw new IOException("unexpected sync frame");
   }
  }
  catch(Exception ex) when(ex is EndOfStreamException or OperationCanceledException or ObjectDisposedException){}
 }
 public async ValueTask DisposeAsync(){_stop.Cancel();_listener.Stop();await Completed;_data.Dispose();_stop.Dispose();}
}
