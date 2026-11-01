using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DashFile.PluginApi;

namespace HybridTransferPlugin;

public sealed class HybridPlugin : PluginBase, IPluginOperationGuard
{
    private HybridProvider? _provider;
    private IDisposable? _registration;
    public bool IsOperationActive => _provider?.Active == true;
    public string OperationDescription => "双轨文件传输 / 通道检测";
    public override PluginMetadata Metadata => new() { Id="com.dashfile.hybridtransfer", Name="双轨快传", Version="1.0.0", Author="EternalDash · 原项目 weixiansen574", Description="USB 与局域网多通道文件传输，逐文件 SHA-256 校验" };
    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        if (Host is not IPluginFileTransferHost transfers) throw new NotSupportedException("双轨快传需要 DashFile 1.2.0 / API 3。");
        _provider=new HybridProvider(Host.DataDirectory,Host.Log.Info) { IsEnabled=Host.Settings.Get("enabled",true), Wifi=Host.Settings.Get("wifi",true), MaxRails=3 };
        _registration=transfers.RegisterFileTransferProvider(_provider);
        Host.AddPage(new PluginPage { Id="hybrid-transfer",Title="双轨快传",Description="USB + Wi-Fi · 完整性校验",Icon="⇄",CreateViewAsync=(_,_)=>Task.FromResult<Control>(CreatePage(transfers)) });
        return Task.CompletedTask;
    }
    private Control CreatePage(IPluginFileTransferHost transfers)
    {
        var provider=_provider!;
        TextBlock Text(string text,int size=14)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap};
        var content=new StackPanel { Spacing=18, Margin=new Thickness(24),MaxWidth=880,HorizontalAlignment=HorizontalAlignment.Stretch };
        var title=Text("让 USB 与 Wi-Fi 一起传",24);title.FontWeight=FontWeight.SemiBold;content.Children.Add(title);
        content.Children.Add(Text("安装并启用后，设备文件的上传、下载及拖出导出会优先使用此插件。无需安装手机 APK；首次传输会启动临时服务。"));
        var enabled=new CheckBox { Content="默认接管设备文件上传与下载",IsChecked=provider.IsEnabled };
        enabled.IsCheckedChanged+=(_,_)=>{provider.IsEnabled=enabled.IsChecked==true;Host.Settings.Set("enabled",provider.IsEnabled);Host.Settings.Save();};content.Children.Add(enabled);
        var wifi=new CheckBox { Content="自动叠加同一局域网中的 Wi-Fi 通道",IsChecked=provider.Wifi };
        wifi.IsCheckedChanged+=(_,_)=>{provider.Wifi=wifi.IsChecked==true;Host.Settings.Set("wifi",provider.Wifi);Host.Settings.Save();};content.Children.Add(wifi);
        content.Children.Add(Text("USB 保持连接，手机和电脑连接同一局域网。Wi-Fi 不可达时仍使用 USB 单通道，并如实显示通道；设备临时服务无法启动时，宿主会提示并使用原有 ADB 传输。已开始的传输失败不会自动重试覆盖。"));
        var status=Text(provider.ChannelSummary);status.FontWeight=FontWeight.SemiBold;content.Children.Add(status);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};
        var probe=new Button { Content="检测传输通道",Padding=new Thickness(18,10) };
        var stop=new Button { Content="取消插件传输",Padding=new Thickness(18,10) };
        probe.Classes.Add("tool");probe.Classes.Add("pri");stop.Classes.Add("tool");
        probe.Click+=async(_,_)=>
        {
            if(!Host.Device.IsConnected){status.Text="请连接处于 Android / Recovery 的 ADB 设备，并完成 USB 调试授权。";return;}
            probe.IsEnabled=false;status.Text="正在建立加密通道…";
            try{await provider.Probe(new(Host.Device.Serial,Host.Device.IsRoot,transfers.AdbExecutablePath,PluginTransferDirection.Download,"/sdcard/",""),CancellationToken.None);status.Text=provider.ChannelSummary;}
            catch(OperationCanceledException){status.Text="已取消检测。";}
            catch(Exception ex){status.Text="通道未就绪："+ex.Message;}
            finally{probe.IsEnabled=true;}
        };
        stop.Click+=(_,_)=>{provider.Stop();status.Text="正在取消；已完成的文件保留，未完成的临时文件会清理。";};
        actions.Children.Add(probe);actions.Children.Add(stop);content.Children.Add(actions);
        content.Children.Add(new Separator());
        content.Children.Add(Text("原项目：HybridFileXfer（多轨快传，原双轨快传）\n原作者：weixiansen574\nhttps://github.com/weixiansen574/HybridFileXfer\n许可证：GPL-3.0-or-later",14));
        content.Children.Add(Text("DashFile 适配：EternalDash。参考原项目的 1 MiB 分块与多通道调度方式，新增主机控制协议、证书固定 TLS、设备身份检查、临时文件原子提交与 SHA-256 校验；与原项目客户端的通信协议不兼容。源码与修改说明见插件包中的 UPSTREAM.md。"));
        return new ScrollViewer { Content=content,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }
    public override async Task ShutdownAsync(CancellationToken cancellationToken=default)
    {
        _registration?.Dispose();_registration=null;
        if(_provider!=null)await _provider.DisposeAsync();_provider=null;
    }
}
