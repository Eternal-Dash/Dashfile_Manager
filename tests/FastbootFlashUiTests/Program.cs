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
var render = args.Length == 2 && args[0] == "--render";
var builder = AppBuilder.Configure<UiTestApp>();
if (render) builder.UseSkia();
builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = !render }).SetupWithoutStarting();
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
var window = new Window { Width = 1900, Height = 720, Content = new Grid { ColumnDefinitions = new("*,*"), Children = { mainPage, managementPage } } };
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
var plugin = new FlashPlugin();
Wait(plugin.InitializeAsync(host));
var aboutPage = hostState.Pages.Single(page => page.Id == "about");
var aboutView = aboutPage.CreateViewAsync(host, CancellationToken.None).GetAwaiter().GetResult();
var aboutWindow = new Window { Width = 680, Height = 600, Content = aboutView };
aboutWindow.Show();
Dispatcher.UIThread.RunJobs();
var aboutText = aboutView.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
Check("about page displays the plugin manifest version and host requirement",
    aboutText.Contains(plugin.Metadata.Version) && aboutText.Contains("v2（宿主 1.1.0 及以上）"));
aboutWindow.Close();
Wait(plugin.ShutdownAsync());
Wait(vm.DetectCommand.ExecuteAsync(null)); Check("single target selected automatically", vm.SelectedDevice?.Serial == "SIM-ONLY");
Check("automatic target label includes serial", vm.TargetDisplay.Contains("SIM-ONLY"));
Wait(vm.InspectCommand.ExecuteAsync(null)); Check("mocked 1000 partition catalog visible", vm.Partitions.Count == 1000 && !vm.CatalogEmpty && vm.CanUsePartitions);
var list = mainPage.FindControl<ListBox>("PartitionList")!;
var planSection = mainPage.FindControl<Expander>("PlanSection")!;
var operations = mainPage.FindControl<Border>("OperationsCard")!;
var sections = mainPage.GetVisualDescendants().OfType<Expander>().ToArray();
bool Fits(Control control, Control parent)
{
    var position = control.TranslatePoint(default, parent);
    return position is { } point && point.X >= -1 && point.Y >= -1 &&
        point.X + control.Bounds.Width <= parent.Bounds.Width + 1 &&
        point.Y + control.Bounds.Height <= parent.Bounds.Height + 1;
}
Check("partition table virtualizes long catalogs", list.GetVisualDescendants().OfType<ListBoxItem>().Count() is > 0 and < 100);
vm.Filter = "p000"; vm.SelectAllCommand.Execute(null); Check("select all respects filtered rows", vm.SelectedCount == 10);
vm.SelectNoneCommand.Execute(null); Check("clear selection clears hidden and visible rows", vm.SelectedCount == 0);
vm.Partitions[0].Selected = true;
Wait(vm.FlashSelectedCommand.ExecuteAsync(null)); Check("selected row without image is never silently skipped", !vm.HasPlan && vm.Status.Contains("尚未选择镜像"));
hostState.PickerResult = Path.Combine(hostState.DirectoryPath, "row image.img"); File.WriteAllBytes(hostState.PickerResult, new byte[64]);
Wait(vm.PickRowImageCommand.ExecuteAsync(vm.Partitions[0])); Check("row picker stores filename and selects row", vm.Partitions[0].HasImage && vm.Partitions[0].Selected);
Wait(vm.FlashSelectedCommand.ExecuteAsync(null)); Check("row batch produces preview without write", vm.HasPlan && planSection.IsExpanded && runner.Writes == 0);
vm.Partitions[0].ImagePath = "changed.img"; Check("changing row image invalidates old preview", !vm.HasPlan);
var updates = 0; vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.SelectedCount)) updates++; };
vm.Filter = ""; updates = 0; var selectWatch = Stopwatch.StartNew(); vm.SelectAllCommand.Execute(null); selectWatch.Stop();
Check("bulk select coalesces catalog notifications", vm.SelectedCount == 1000 && updates < 10 && selectWatch.ElapsedMilliseconds < 1000);
vm.SelectNoneCommand.Execute(null);
vm.Filter = "no-such-partition"; Check("empty search provides a specific hint", vm.ListEmpty && !vm.CatalogEmpty && vm.EmptyTitle.Contains("没有匹配")); vm.Filter = "";
Check("exactly two workspace cards and no outer page scroll",
    mainPage.FindControl<Grid>("Workspace")!.Children.Count == 2 &&
    mainPage.FindControl<Border>("PartitionCard") != null &&
    !mainPage.GetVisualDescendants().OfType<TabControl>().Any());
vm.DeviceModeIndex = 1; Dispatcher.UIThread.RunJobs(); Check("device mode shared between both views", vm.DeviceSelectionEnabled && managementPage.FindControl<ComboBox>("DevicePicker")!.IsEnabled);
runner.Multiple = true; vm.DeviceModeIndex = 0; Wait(vm.DetectCommand.ExecuteAsync(null)); Check("automatic mode refuses ambiguous device selection", vm.SelectedDevice == null && vm.Status.Contains("多台设备"));
vm.DeviceModeIndex = 1; vm.SelectedDevice = vm.Devices[1]; Dispatcher.UIThread.RunJobs(); Check("manual device choice remains explicit", vm.SelectedDevice.Serial == "SIM-SECOND");
((Grid)window.Content!).Children.Remove(managementPage);
((Grid)window.Content!).ColumnDefinitions.Clear();
foreach (var appearance in new[] { "light", "dark", "hyprland" })
{
    ThemeService.ApplyAppearance(appearance == "light" ? "light" : "dark", "m3", "#FFFFFF");
    ThemeService.ApplyHyprland(appearance == "hyprland", "m3", "#FFFFFF");
    foreach (var size in new[] { new Size(928, 510), new Size(1103, 570), new Size(1468, 760) })
    {
        window.Width = size.Width;
        window.Height = size.Height;

        Dispatcher.UIThread.RunJobs();
        var ws = mainPage.FindControl<Grid>("Workspace")!;
        Check($"full-width table fits {appearance} at {size.Width}x{size.Height}",
            ws.Bounds.Height > 0 && ws.Bounds.Height <= size.Height &&
            list.Bounds.Width > 400 && list.Bounds.Height > 180 && Fits(operations, ws));
        foreach (var section in sections)
        {
            section.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Check($"accordion fits {section.Name} at {size.Width} in {appearance}",
                sections.Count(item => item.IsExpanded) == 1 &&
                sections.All(item => Fits(item, operations)) &&
                section.GetVisualDescendants().OfType<Button>().All(button => Fits(button, operations)) &&
                section.GetVisualDescendants().OfType<TextBox>().All(box => Fits(box, operations)));
        }
    }
}
Check("device tools retain exactly five reboot targets",
    mainPage.FindControl<Expander>("RebootSection")!.GetVisualDescendants().OfType<Button>().Count(b => b.CommandParameter is string) == 5);
if (render)
{
    ThemeService.ApplyHyprland(false, "m3", "#FFFFFF");
    ThemeService.ApplyAppearance("light", "m3", "#FFFFFF");
    window.Width = 1103;
    window.Height = 570;
    vm.DeviceModeIndex = 0;
    runner.Multiple = false;
    Wait(vm.DetectCommand.ExecuteAsync(null));
    Wait(vm.InspectCommand.ExecuteAsync(null));
    sections[0].IsExpanded = true;
    Dispatcher.UIThread.RunJobs();
    var settle = Stopwatch.StartNew();
    while (settle.ElapsedMilliseconds < 240)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frameWait = new CancellationTokenSource(8);
        Dispatcher.UIThread.MainLoop(frameWait.Token);
    }
    using var frame = window.CaptureRenderedFrame();
    frame?.Save(args[1]);
    ThemeService.ApplyHyprland(true, "m3", "#FFFFFF");
    settle.Restart();
    while (settle.ElapsedMilliseconds < 300)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frameWait = new CancellationTokenSource(8);
        Dispatcher.UIThread.MainLoop(frameWait.Token);
    }
    using var darkFrame = window.CaptureRenderedFrame();
    darkFrame?.Save(Path.Combine(Path.GetDirectoryName(args[1])!, "DashFile_双卡片_Hyprland检查.png"));
}
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
    public List<PluginPage> Pages { get; } = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_DataDirectory": Directory.CreateDirectory(DirectoryPath); return DirectoryPath;
            case "get_Ui": var ui = DispatchProxy.Create<IPluginUi, UiProxy>(); ((UiProxy)(object)ui).State = this; return ui;
            case "get_Log": return DispatchProxy.Create<IPluginLog, LogProxy>();
            case "AddPage": Pages.Add((PluginPage)args![0]!); return null;
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
            "max-fetch-size" => "max-fetch-size: 0x1000000",
            "all" => "product: ui-test\ncurrent-slot: a\nis-userspace: no\nunlocked: yes\n" + string.Join("\n", Enumerable.Range(0, 1000).Select(i => $"partition-size:p{i:D4}: 0x1000")),
            _ when key.StartsWith("partition-size:") => key + ": 0x1000",
            _ when key.StartsWith("is-logical:") || key.StartsWith("has-slot:") => key + ": no",
            _ => ""
        };
        return Task.FromResult(new ToolResult(0, output));
    }
}
