using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DashFile.Services;

namespace DashFile.Views;

public partial class TransferProgressWindow : Window
{
    private FileTransferOperation? _operation;
    private readonly DispatcherTimer _timer;
    private DateTime? _finishedAt;
    private bool _canClose;
    public TransferProgressWindow(FileTransferOperation operation) : this() { _operation=operation;Refresh(); }
    public TransferProgressWindow()
    {
        AvaloniaXamlLoader.Load(this);DashFile.Controls.ViewAnimator.AttachWindow(this);
        _timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(150) };
        _timer.Tick+=(_,_)=>Refresh();Opened+=(_,_)=>{Refresh();_timer.Start();};Closed+=(_,_)=>_timer.Stop();
        Closing+=(_,e)=>{if(_canClose||_operation==null)return;e.Cancel=true;_operation.Cancel();Refresh();};
        KeyDown+=(_,e)=>{if(e.Key==Avalonia.Input.Key.Escape){e.Handled=true;CancelOrClose();}};
        Refresh();
    }
    private void OnCancel(object? sender,RoutedEventArgs e)=>CancelOrClose();
    private void CancelOrClose(){if(_canClose||_operation==null)Close();else{_operation.Cancel();Refresh();}}
    private void Refresh()
    {
        if(_operation==null)return;
        var s=_operation.Snapshot;
        this.GetControl<TextBlock>("DirectionText").Text=s.Direction;
        this.GetControl<TextBlock>("FileNameText").Text=s.FileName;
        this.GetControl<TextBlock>("ChannelText").Text=s.Channels;
        this.GetControl<TextBlock>("PhaseText").Text=s.Cancelling&&!s.Finished?"正在停止并清理未完成文件…":s.Phase;
        var bar=this.GetControl<ProgressBar>("TransferBar");bar.IsIndeterminate=s.Total==0&&!s.Finished;
        var percent=s.Total>0?Math.Clamp(s.Completed*100d/s.Total,0,100):s.Finished?100:0;
        bar.Value=percent;
        this.GetControl<TextBlock>("PercentText").Text=s.Total>0||s.Finished?$"{percent:F1}%":"—";
        this.GetControl<TextBlock>("BytesText").Text=$"{Size(s.Completed)} / {(s.Total>0?Size(s.Total):"正在读取大小")}";
        this.GetControl<TextBlock>("SpeedText").Text=s.Finished&&!s.Cancelling&&s.Error.Length==0&&s.ElapsedSeconds>0?$"平均 {Size(s.Completed/s.ElapsedSeconds)}/s":s.BytesPerSecond>0?$"{Size(s.BytesPerSecond)}/s":"— MB/s";
        var remaining=s.BytesPerSecond>0&&s.Total>s.Completed?TimeSpan.FromSeconds((s.Total-s.Completed)/s.BytesPerSecond).ToString(@"hh\:mm\:ss"):"—";
        this.GetControl<TextBlock>("TimeText").Text="已用 "+TimeSpan.FromSeconds(s.ElapsedSeconds).ToString(@"hh\:mm\:ss")+"  ·  预计剩余 "+remaining;
        var button=this.GetControl<Button>("CancelButton");button.IsEnabled=!s.Cancelling||s.Finished;
        if(!s.Finished)return;
        _canClose=true;button.Content="关闭";
        if(s.Error.Length>0){this.GetControl<TextBlock>("NoticeText").Text=s.Error;return;}
        if(s.Cancelling){Close();return;}
        _finishedAt??=DateTime.UtcNow;if(DateTime.UtcNow-_finishedAt>TimeSpan.FromMilliseconds(600))Close();
    }
    private static string Size(double bytes)=>bytes>=1073741824?$"{bytes/1073741824:F2} GB":bytes>=1048576?$"{bytes/1048576:F2} MB":bytes>=1024?$"{bytes/1024:F1} KB":$"{bytes:F0} B";
}
