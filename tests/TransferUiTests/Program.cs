using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using DashFile.PluginApi;
using DashFile.Services;
using DashFile.Views;
using System.Diagnostics;

AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
int failed=0,passed=0;
void Check(string name,bool ok){Console.WriteLine((ok?"PASS ":"FAIL ")+name);if(ok)passed++;else failed++;}
void Pump(int ms=220){using var stop=new CancellationTokenSource(ms);Dispatcher.UIThread.MainLoop(stop.Token);}
var operations=new FileTransferOperations();
var request=new PluginTransferRequest("TEST",false,"none",PluginTransferDirection.Download,"/sdcard/source.zip","C:/test/download.zip");
using(var op=operations.Begin(request,CancellationToken.None))
{
 var window=new TransferProgressWindow(op);window.Show();Dispatcher.UIThread.RunJobs();
 op.Report(new(25,100,"USB + wlan0","download.zip"));Pump();
 Check("visible progress bar displays actual bytes",window.GetControl<ProgressBar>("TransferBar").Value==25&&window.GetControl<TextBlock>("PercentText").Text=="25.0%");
 op.Report(new(20,100,"USB + wlan0","download.zip"));Check("out of order rail events cannot rewind progress",op.Snapshot.Completed==25);
 Thread.Sleep(320);op.Report(new(50,100,"USB + wlan0","download.zip"));Pump();
 Check("live speed is measured from actual byte deltas",op.Snapshot.BytesPerSecond>0&&!window.GetControl<TextBlock>("SpeedText").Text!.StartsWith('—'));
 window.Close();Pump();
 Check("window close requests cancellation",op.Token.IsCancellationRequested);
 Check("window remains open while cleanup is pending",window.IsVisible&&operations.ActiveCount==1&&!op.Snapshot.Finished);
 Check("cleanup notice replaces transfer state",window.GetControl<TextBlock>("PhaseText").Text!.Contains("清理"));
 operations.End(op,new OperationCanceledException());Pump();
 Check("window closes only after cleanup finishes",!window.IsVisible&&operations.ActiveCount==0);
}
using(var op=operations.Begin(request,CancellationToken.None))
{
 var window=new TransferProgressWindow(op);window.Show();Dispatcher.UIThread.RunJobs();
 window.GetControl<Button>("CancelButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Pump();
 Check("cancel button uses the same cleanup barrier",op.Token.IsCancellationRequested&&window.IsVisible);
 operations.End(op,new IOException("未完成文件清理失败：/sdcard/.dashfile-test.partial"));Pump();
 Check("failed cleanup never pretends cancellation succeeded",window.IsVisible&&window.GetControl<TextBlock>("NoticeText").Text!.Contains("清理失败"));
 window.Close();
}
using(var op=operations.Begin(request,CancellationToken.None))
{
 var window=new TransferProgressWindow(op);window.Show();op.Report(new(100,100,"ADB · USB","download.zip","已完成"));operations.End(op,null);Pump(900);
 Check("completed transfer closes automatically",!window.IsVisible);
}
Console.WriteLine($"{passed} passed, {failed} failed; UI only, no hardware.");return failed==0?0:1;
