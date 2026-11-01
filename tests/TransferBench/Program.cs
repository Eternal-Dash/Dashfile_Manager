using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DashFile.Services;
using HybridTransferPlugin;

if(args.Length is <5 or >6||args[0]!="--zip")throw new ArgumentException("Explicit hardware opt-in: --zip <source> <adb> <serial> <output-directory>");
var source=Path.GetFullPath(args[1]);var output=Path.GetFullPath(args[4]);Directory.CreateDirectory(output);
using var sourceLock=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,true);
var expected=Convert.ToHexString(await SHA256.HashDataAsync(sourceLock));var length=sourceLock.Length;
Console.WriteLine($"Source: {length} bytes; SHA-256 {expected}");
using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(20));
var runner=new AdbRunner(args[2]);var registry=new FileTransferRegistry();
await using var provider=new HybridProvider(output,Console.WriteLine){ThresholdBytes=TransferThreshold.Megabyte};
using var lease=registry.Register("benchmark",provider,100);
var fs=new RemoteFileSystem(runner,args[3],false,registry);
var remoteDir=DevicePaths.Download+"/.dashfile-zip-test-"+Guid.NewGuid().ToString("N");var remote=Sh.Join(remoteDir,Path.GetFileName(source));
var rows=new List<object>();var cancelDirs=new List<string>();FileTransferOperation? current=null;registry.Operations.Started+=op=>current=op;
var setup=await fs.ExecuteAsync("mkdir -p -- "+Sh.Q(remoteDir),false,deadline.Token);if(!setup.Ok)throw new IOException(setup.All);
async Task<string> HashLocal(string path){using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,true);return Convert.ToHexString(await SHA256.HashDataAsync(f,deadline.Token));}
async Task Measure(string mode,string direction,Func<Task> transfer)
{
    Console.WriteLine($"START {mode} {direction}");var before=provider.Traffic;var watch=Stopwatch.StartNew();current=null;
    var task=transfer();double firstAt=0,lastAt=0;long firstBytes=0,lastBytes=0;var lastPrint=0d;
    while(!task.IsCompleted)
    {
        var s=current?.Snapshot;
        if(s is { Completed:>0,Phase:"传输中" })
        {
            if(firstAt==0){firstAt=watch.Elapsed.TotalSeconds;firstBytes=s.Completed;}
            lastAt=watch.Elapsed.TotalSeconds;lastBytes=s.Completed;
        }
        if(watch.Elapsed.TotalSeconds-lastPrint>=3){lastPrint=watch.Elapsed.TotalSeconds;Console.WriteLine($"PROGRESS {mode} {direction}: {s?.Completed/1048576d:F1}/{length/1048576d:F1} MiB · {s?.Phase}");}
        await Task.WhenAny(task,Task.Delay(100,deadline.Token));
    }
    await task;watch.Stop();
    string actual;
    if(direction=="upload")
    {
        var hash=await runner.RunAsync(new[]{"-s",args[3],"shell","sha256sum -- "+Sh.Q(remote)},TimeSpan.FromMinutes(3),deadline.Token);
        if(!hash.Ok)throw new IOException(hash.All);actual=hash.StdOut.Trim().Split(' ')[0];
    }
    else actual=await HashLocal(Path.Combine(output,"download.zip"));
    if(!actual.Equals(expected,StringComparison.OrdinalIgnoreCase))throw new IOException("SHA-256 mismatch");
    var after=provider.Traffic;
    var traffic=after.ToDictionary(x=>x.Key,x=>x.Value-(before.TryGetValue(x.Key,out var v)?v:0));
    // Reconnecting can reset counters even when the rail names are unchanged.
    if(provider.IsEnabled&&traffic.Values.Sum()!=length)traffic=after.ToDictionary(x=>x.Key,x=>x.Value);
    if(provider.IsEnabled&&traffic.Values.Sum()!=length)throw new IOException("Unexpected per-session traffic accounting");
    var channels=current!.Snapshot.Channels;
    var row=new {Mode=mode,Direction=direction,Bytes=length,Seconds=watch.Elapsed.TotalSeconds,EndToEndMiBps=length/1048576d/watch.Elapsed.TotalSeconds,
        SampledDataMiBps=lastAt>firstAt?(lastBytes-firstBytes)/1048576d/(lastAt-firstAt):0,Channels=channels,Traffic=traffic,Sha256=actual.ToUpperInvariant()};
    rows.Add(row);await File.WriteAllTextAsync(Path.Combine(output,"results.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("PASS "+JsonSerializer.Serialize(row));
}
try
{
    var modes=args.Length==6&&args[5]=="--dual-only"?new[]{"Hybrid-Dual"}:new[]{"ADB","Hybrid-USB","Hybrid-Dual"};
    foreach(var mode in modes)
    {
        provider.IsEnabled=mode!="ADB";provider.Wifi=mode=="Hybrid-Dual";
        await Measure(mode,"upload",()=>fs.UploadAsync(source,remoteDir,deadline.Token));
        await Measure(mode,"download",()=>fs.DownloadAsync(remote,Path.Combine(output,"download.zip"),deadline.Token));
        File.Delete(Path.Combine(output,"download.zip"));
    }
    foreach(var hybrid in new[]{false,true})
    {
        provider.IsEnabled=hybrid;provider.Wifi=true;
        var folder=remoteDir+"/cancel-"+(hybrid?"hybrid":"adb");cancelDirs.Add(folder);
        var make=await fs.ExecuteAsync("mkdir -- "+Sh.Q(folder),false,deadline.Token);if(!make.Ok)throw new IOException(make.All);
        foreach(var direction in new[]{"upload","download"})
        {
            var local=Path.Combine(output,"cancel-download.zip");await File.WriteAllTextAsync(local,"keep existing file");current=null;
            var task=direction=="upload"?fs.UploadAsync(source,folder,deadline.Token):fs.DownloadAsync(remote,local,deadline.Token);
            while(!task.IsCompleted&&(current==null||current.Snapshot.Completed<32*1024*1024))await Task.WhenAny(task,Task.Delay(30,deadline.Token));
            if(task.IsCompleted)throw new IOException("Transfer completed before cancellation could be exercised");
            current!.Cancel();var stop=Stopwatch.StartNew();
            try{await task;throw new IOException("Cancellation did not stop transfer");}catch(OperationCanceledException){}
            var list=await fs.ExecuteAsync("ls -A -- "+Sh.Q(folder),false,deadline.Token);
            if(!list.Ok||!string.IsNullOrWhiteSpace(list.StdOut)||Directory.GetFiles(output,"*.partial").Any()||await File.ReadAllTextAsync(local)!="keep existing file")throw new IOException("Cancellation cleanup or preservation failed");
            Console.WriteLine($"PASS cancellation {(hybrid?"Hybrid":"ADB")} {direction}; cleanup {stop.Elapsed.TotalSeconds:F2}s; no partial or partial final file, original destination preserved");File.Delete(local);
        }
    }
}
finally
{
    var commands=new List<string>{"rm -f -- "+Sh.Q(remote)};commands.AddRange(cancelDirs.Select(dir=>"rmdir -- "+Sh.Q(dir)));commands.Add("rmdir -- "+Sh.Q(remoteDir));
    var command=string.Join("; ",commands);
    var cleanup=await fs.ExecuteAsync(command,false,CancellationToken.None);Console.WriteLine("REMOTE TEST CLEANUP "+cleanup.ExitCode);
}
Console.WriteLine("ZIP BIDIRECTIONAL BENCHMARK PASS");
