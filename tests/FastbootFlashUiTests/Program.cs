using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Threading;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DashFile.PluginApi;
using DashFile.Services;
using FastbootFlashPlugin;

Console.WriteLine("Headless startup");
AppBuilder.Configure<UiTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
Console.WriteLine("Headless ready");
if (args.Contains("--radio-repro"))
{
    var radioVm = new RadioModel();
    StackPanel Pair() => new() { Children = {
        new RadioButton { GroupName = "dev", Content = "自动", [!RadioButton.IsCheckedProperty] = new Binding("AutoDetect") },
        new RadioButton { GroupName = "dev", Content = "指定", [!RadioButton.IsCheckedProperty] = new Binding("!AutoDetect") }
    }};
    Console.WriteLine("Constructing legacy radio bindings");
    var page1 = Pair(); var page2 = Pair();
    var radioWindow = new Window { Width = 1000, Height = 600, DataContext = radioVm, Content = new StackPanel { Children = { page1, page2 } } };
    Console.WriteLine("Attaching two views with shared device radio group...");
    radioWindow.Show(); Dispatcher.UIThread.RunJobs();
    Console.WriteLine("Radio views attached successfully"); radioWindow.Close(); return 0;
}
var failed = 0; var passed = 0;
void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (ok) passed++; else failed++; }
using var timeout = new CancellationTokenSource();
_ = Task.Run(async () => { try { await Task.Delay(60000, timeout.Token); Console.Error.WriteLine("FAIL UI watchdog: dispatcher did not recover"); Environment.Exit(99); } catch (OperationCanceledException) { } });
var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, ArgumentList = { "--radio-repro" } })!;
var childLog = child.StandardOutput.ReadToEndAsync();
if (!child.WaitForExit(10000)) { child.Kill(true); child.WaitForExit(); Check("legacy radio feedback diagnosed", false); }
else Check("legacy radio feedback diagnosed", child.ExitCode == 86 && childLog.GetAwaiter().GetResult().Contains("200+ recursive"));
var host = DispatchProxy.Create<IPluginHost, HostProxy>(); var hostState = (HostProxy)(object)host;
var runner = new UiRunner(); var vm = new FlashViewModel(host, new FlashEngine(runner));
var mainPage = new FlashPage { DataContext = vm };
var managementPage = new FlashPage { DataContext = vm };
var window = new Window { Width = 1540, Height = 720, Content = new Grid { ColumnDefinitions = new("*,*"), Children = { mainPage, managementPage } } };
Grid.SetColumn(managementPage, 1);
window.Show(); Dispatcher.UIThread.RunJobs();
Check("two plugin views attach without radio feedback", vm.DeviceModeIndex == 0 && !mainPage.GetVisualDescendants().OfType<RadioButton>().Any());
Check("automatic mode has an empty-state target hint", vm.TargetDisplay.Contains("检测设备"));
var initialCalls = runner.Calls;
Check("opening page never probes or writes hardware", initialCalls == 0);
void Wait(Task task)
{
    var sw = Stopwatch.StartNew();
    while (!task.IsCompleted && sw.ElapsedMilliseconds < 10000) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
    if (!task.IsCompleted) throw new TimeoutException("UI task timed out");
    task.GetAwaiter().GetResult(); Dispatcher.UIThread.RunJobs();
}
Wait(vm.DetectCommand.ExecuteAsync(null)); Check("single target selected automatically", vm.SelectedDevice?.Serial == "SIM-ONLY");
Check("automatic target label includes serial", vm.TargetDisplay.Contains("SIM-ONLY"));
Wait(vm.InspectCommand.ExecuteAsync(null)); Check("mocked 1000 partition catalog visible", vm.Partitions.Count == 1000 && !vm.CatalogEmpty && vm.CanUsePartitions);
var list = mainPage.FindControl<ListBox>("PartitionList")!;
Check("partition table virtualizes long catalogs", list.GetVisualDescendants().OfType<ListBoxItem>().Count() is > 0 and < 100);
vm.Filter = "p000"; vm.SelectAllCommand.Execute(null); Check("select all respects filtered rows", vm.SelectedCount == 10);
vm.SelectNoneCommand.Execute(null); Check("clear selection clears hidden and visible rows", vm.SelectedCount == 0);
vm.Partitions[0].Selected = true;
Wait(vm.FlashSelectedCommand.ExecuteAsync(null)); Check("selected row without image is never silently skipped", !vm.HasPlan && vm.Status.Contains("尚未选择镜像"));
hostState.PickerResult = Path.Combine(hostState.DirectoryPath, "row image.img"); File.WriteAllBytes(hostState.PickerResult, new byte[64]);
Wait(vm.PickRowImageCommand.ExecuteAsync(vm.Partitions[0])); Check("row picker stores filename and selects row", vm.Partitions[0].HasImage && vm.Partitions[0].Selected);
Wait(vm.FlashSelectedCommand.ExecuteAsync(null)); Check("row batch produces preview without write", vm.HasPlan && vm.IsPlanOpen && runner.Writes == 0);
vm.Partitions[0].ImagePath = "changed.img"; Check("changing row image invalidates old preview", !vm.HasPlan);
var updates = 0; vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.SelectedCount)) updates++; };
vm.Filter = ""; updates = 0; var selectWatch = Stopwatch.StartNew(); vm.SelectAllCommand.Execute(null); selectWatch.Stop();
Check("bulk select coalesces catalog notifications", vm.SelectedCount == 1000 && updates < 10 && selectWatch.ElapsedMilliseconds < 1000);
vm.SelectNoneCommand.Execute(null);
vm.Filter = "no-such-partition"; Check("empty search provides a specific hint", vm.ListEmpty && !vm.CatalogEmpty && vm.EmptyTitle.Contains("没有匹配")); vm.Filter = "";
var sections = mainPage.GetVisualDescendants().OfType<Expander>().ToArray();
var layoutEvents = 0; mainPage.LayoutUpdated += (_, _) => layoutEvents++;
for (var i = 0; i < 40; i++) { foreach (var e in sections) e.IsExpanded = i % 2 == 0; Dispatcher.UIThread.RunJobs(); }
Check("all sections collapse and expand without layout loop", sections.Length == 8 && layoutEvents < 1000);
vm.DeviceModeIndex = 1; Dispatcher.UIThread.RunJobs(); Check("device mode shared between both views", vm.DeviceSelectionEnabled && managementPage.FindControl<ComboBox>("DevicePicker")!.IsEnabled);
runner.Multiple = true; vm.DeviceModeIndex = 0; Wait(vm.DetectCommand.ExecuteAsync(null)); Check("automatic mode refuses ambiguous device selection", vm.SelectedDevice == null && vm.Status.Contains("多台设备"));
vm.DeviceModeIndex = 1; vm.SelectedDevice = vm.Devices[1]; Dispatcher.UIThread.RunJobs(); Check("manual device choice remains explicit", vm.SelectedDevice.Serial == "SIM-SECOND");
foreach (var appearance in new[] { "light", "dark" })
{
    ThemeService.ApplyAppearance(appearance, "m3", "#7DD3FC");
    foreach (var size in new[] { new Size(960, 640), new Size(1135, 707), new Size(1500, 900) })
    {
        window.Width = size.Width; window.Height = size.Height; Dispatcher.UIThread.RunJobs();
        var ws = mainPage.FindControl<Grid>("Workspace")!;
        Check($"stable {appearance} layout at {size.Width}x{size.Height}", double.IsFinite(ws.Bounds.Height) && ws.Bounds.Height is >= 560 and < 1000);
    }
}
Check("reboot group has exactly five supported targets", mainPage.FindControl<Expander>("RebootSection")!.GetVisualDescendants().OfType<Button>().Count(b => b.CommandParameter is string) == 5);
window.Close(); timeout.Cancel(); Console.WriteLine($"{passed} UI checks passed, {failed} failed; hardware writes {runner.Writes}"); return failed == 0 ? 0 : 1;

public class UiTestApp : DashFile.App { public override void OnFrameworkInitializationCompleted() { } }
public class RadioModel : INotifyPropertyChanged
{
    private bool _autoDetect = true;
    private int _changes;
    public bool AutoDetect { get => _autoDetect; set { if (_autoDetect != value) { _autoDetect = value; if (++_changes > 200) { Console.WriteLine("CONFIRMED: 200+ recursive radio binding changes\n" + Environment.StackTrace); Environment.Exit(86); } PropertyChanged?.Invoke(this, new(nameof(AutoDetect))); } } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public class HostProxy : DispatchProxy
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "DashFile-UI-" + Guid.NewGuid().ToString("N"));
    public string? PickerResult;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_DataDirectory": Directory.CreateDirectory(DirectoryPath); return DirectoryPath;
            case "get_Ui": var ui = DispatchProxy.Create<IPluginUi, UiProxy>(); ((UiProxy)(object)ui).State = this; return ui;
            case "get_Log": return DispatchProxy.Create<IPluginLog, LogProxy>();
            default: throw new NotSupportedException(method.Name);
        }
    }
}
public class UiProxy : DispatchProxy
{
    public HostProxy State = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "RunOnUiThread": ((Action)args![0]!)(); return null;
            case "OpenFileAsync": return Task.FromResult(State.PickerResult);
            case "ConfirmAsync": return Task.FromResult(false);
            case "PromptAsync": return Task.FromResult<string?>(null);
            default: throw new NotSupportedException(method.Name);
        }
    }
}
public class LogProxy : DispatchProxy { protected override object? Invoke(MethodInfo? method, object?[]? args) => null; }
public sealed class UiRunner : IToolRunner
{
    public bool Multiple; public int Calls; public int Writes;
    public Task<ToolResult> RunAsync(Transport t, IReadOnlyList<string> a, string? f, TimeSpan? timeout, Action<string>? line, CancellationToken ct)
    {
        Calls++;
        if (a.Contains("flash") || a.Contains("erase")) Writes++;
        if (a.SequenceEqual(new[] { "devices" })) return Task.FromResult(new ToolResult(0, t == Transport.Fastboot ? "SIM-ONLY fastboot\n" + (Multiple ? "SIM-SECOND fastboot\n" : "") : ""));
        var key = a[^1]; var output = key switch {
            "all" => "product: ui-test\ncurrent-slot: a\nis-userspace: no\nunlocked: yes\n" + string.Join("\n", Enumerable.Range(0, 1000).Select(i => $"partition-size:p{i:D4}: 0x1000")),
            _ when key.StartsWith("partition-size:") => key + ": 0x1000",
            _ when key.StartsWith("is-logical:") || key.StartsWith("has-slot:") => key + ": no",
            _ => ""
        };
        return Task.FromResult(new ToolResult(0, output));
    }
}
