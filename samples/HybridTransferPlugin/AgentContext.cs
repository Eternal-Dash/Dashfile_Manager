using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using DashFile.PluginApi;

namespace HybridTransferPlugin;

public sealed class AgentContext : IAsyncDisposable
{
    private readonly string _adb,_serial,_remote;
    private readonly bool _root;
    private Process? _process;
    private int _forward;
    public HybridSession? Session { get; private set; }
    public bool IsAlive => _process is { HasExited:false } && Session != null;
    private AgentContext(PluginTransferRequest r)
    { _adb=r.AdbPath;_serial=r.Serial;_root=r.UseRoot;_remote="/data/local/tmp/.dashfile-hfx-"+Guid.NewGuid().ToString("N"); }
    public static string Quote(string value) => "'"+value.Replace("'","'\\''")+"'";
    private ProcessStartInfo StartInfo(IEnumerable<string> args)
    {
        var info=new ProcessStartInfo(_adb) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        info.ArgumentList.Add("-s");info.ArgumentList.Add(_serial);foreach(var a in args)info.ArgumentList.Add(a);return info;
    }
    private async Task<string> Adb(IEnumerable<string> args,CancellationToken ct,int seconds=20)
    {
        using var limit=HybridSession.Deadline(ct,seconds);using var process=new Process {StartInfo=StartInfo(args)};
        process.Start();using var cancel=limit.Token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch{}});
        var stdout=process.StandardOutput.ReadToEndAsync(limit.Token);var stderr=process.StandardError.ReadToEndAsync(limit.Token);
        await Task.WhenAll(process.WaitForExitAsync(limit.Token),stdout,stderr).ConfigureAwait(false);var output=await stdout.ConfigureAwait(false);var error=await stderr.ConfigureAwait(false);
        if(process.ExitCode!=0)throw new IOException("ADB 准备传输通道失败："+error.Trim());return output.Trim();
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification="Factory transfers ownership on success and disposes all acquired resources in catch; deadline sources use using declarations.")]
    public static async Task<AgentContext> Start(PluginTransferRequest request,string dataDir,bool wifi,int maxRails,Action<string>? log,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(request.Serial)||request.Serial.StartsWith('-')||request.Serial.Any(char.IsWhiteSpace)||!File.Exists(request.AdbPath))throw new IOException("必须指定有效 ADB 程序和设备。");
        var context=new AgentContext(request);var local=Path.Combine(dataDir,"bootstrap-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(local);
        try
        {
            var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            using var rsa=RSA.Create(2048);var certificateRequest=new CertificateRequest("CN=dashfile-hybrid.local",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            var san=new SubjectAlternativeNameBuilder();san.AddDnsName("dashfile-hybrid.local");certificateRequest.CertificateExtensions.Add(san.Build());
            certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,false));
            using var certificate=certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddDays(1));
            var pin=SHA256.HashData(certificate.RawData);var p12=Path.Combine(local,"peer.p12");await File.WriteAllBytesAsync(p12,certificate.Export(X509ContentType.Pkcs12,password),ct).ConfigureAwait(false);
            var dex=Path.Combine(local,"agent.dex");
            using(var resource=typeof(AgentContext).Assembly.GetManifestResourceStream("HybridTransferPlugin.Resources.agent.dex") ?? throw new IOException("插件缺少设备传输核心。"))
            using(var output=new FileStream(dex,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))await resource.CopyToAsync(output,ct).ConfigureAwait(false);
            await context.Adb(new[]{"shell","mkdir -p "+Quote(context._remote)+" && chmod 700 "+Quote(context._remote)},ct).ConfigureAwait(false);
            await context.Adb(new[]{"push",dex,context._remote+"/agent.dex"},ct).ConfigureAwait(false);
            await context.Adb(new[]{"push",p12,context._remote+"/peer.p12"},ct).ConfigureAwait(false);
            await context.Adb(new[]{"shell","chmod 600 "+Quote(context._remote+"/peer.p12")},ct).ConfigureAwait(false);
            var command="CLASSPATH="+Quote(context._remote+"/agent.dex")+" app_process / com.dashfile.hybrid.Agent "+Quote(context._remote+"/peer.p12")+" "+Quote(password)+" "+Quote(token)+" "+Quote(request.Serial)+" 0.0.0.0";
            if(request.UseRoot)command="su -c "+Quote(command);
            context._process=new Process {StartInfo=context.StartInfo(new[]{"shell",command})};context._process.Start();
            var errors=new StringBuilder();
            var errorReader = context._process.StandardError;
            async Task DrainErrors()
            { try { while(await errorReader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is {} line) { lock(errors)if(errors.Length<8192)errors.AppendLine(line); } }catch(Exception e) when (e is IOException or ObjectDisposedException){} }
            _=DrainErrors();
            int port=0;
            using(var ready=HybridSession.Deadline(ct,15))
                while(await context._process.StandardOutput.ReadLineAsync(ready.Token).ConfigureAwait(false) is {} line)
                {var match=Regex.Match(line,@"^DHFX_READY (\d+)$");if(match.Success){port=int.Parse(match.Groups[1].Value);break;}}
            if(port is <=0 or >65535) {lock(errors)throw new IOException("设备传输核心未启动："+errors.ToString());}
            var forwarded=await context.Adb(new[]{"forward","tcp:0","tcp:"+port},ct).ConfigureAwait(false);
            if(!int.TryParse(forwarded,out context._forward)||context._forward is <=0 or >65535)throw new IOException("ADB 没有返回有效的转发端口。");
            context.Session=await HybridSession.Connect(context._forward,port,request.Serial,request.UseRoot,token,pin,wifi,maxRails,log,ct).ConfigureAwait(false);
            log?.Invoke("双轨快传连接完成："+context.Session.Channels);return context;
        }
        catch {await context.DisposeAsync().ConfigureAwait(false);throw;}
        finally {foreach(var file in Directory.EnumerateFiles(local))try{File.Delete(file);}catch{}try{Directory.Delete(local);}catch{}}
    }
    public async ValueTask DisposeAsync()
    {
        if(Session!=null){await Session.DisposeAsync().ConfigureAwait(false);Session=null;}
        try{if(_process is {HasExited:false})_process.Kill(true);}catch{} _process?.Dispose();_process=null;
        try{if(_forward>0)await Adb(new[]{"forward","--remove","tcp:"+_forward},CancellationToken.None,3).ConfigureAwait(false);}catch{} _forward=0;
        // Delete only this session's known helper files, never recursively remove a computed directory.
        if(Regex.IsMatch(_remote,@"^/data/local/tmp/\.dashfile-hfx-[a-f0-9]{32}$"))
            try{await Adb(new[]{"shell","rm -f "+Quote(_remote+"/agent.dex")+" "+Quote(_remote+"/peer.p12")+"; rmdir "+Quote(_remote)+" 2>/dev/null"},CancellationToken.None,3).ConfigureAwait(false);}catch{}
    }
}
