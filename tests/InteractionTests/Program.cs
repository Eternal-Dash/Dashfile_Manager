using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DashFile.Controls;

var render = args.Length == 2 && args[0] == "--render";
var builder = AppBuilder.Configure<DashFile.App>();
if (render) builder.UseSkia();
builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = !render }).SetupWithoutStarting();
int passed=0, failed=0;
void Check(string name,bool ok){Console.WriteLine((ok?"PASS ":"FAIL ")+name);if(ok)passed++;else failed++;}
void Pump(int ms=160){var watch=System.Diagnostics.Stopwatch.StartNew();while(watch.ElapsedMilliseconds<ms){AvaloniaHeadlessPlatform.ForceRenderTimerTick();using var stop=new CancellationTokenSource(8);Dispatcher.UIThread.MainLoop(stop.Token);}}
void Wheel(Window w,double delta)=>w.MouseWheel(new Point(150,110),new Vector(0,delta),RawInputModifiers.None);
var content=new Border{Height=3000,Background=Brushes.White};
var sv=new ScrollViewer{Content=content};SmoothScroll.Attach(sv);
var w=new Window{Width=400,Height=300,Content=sv};w.Show();Pump();
Check("real scroll viewport has overflow",sv.Extent.Height>sv.Viewport.Height+1000);
sv.Offset=new Vector(0,500);Wheel(w,-1);Pump(35);
Check("coarse wheel moves before animation ends",sv.Offset.Y>500&&sv.Offset.Y<565);
var turning=sv.Offset.Y;Wheel(w,1);Pump(35);
Check("reverse input changes direction without chasing old target",sv.Offset.Y<turning);
Pump();
sv.Offset=new Vector(0,500);Wheel(w,-1);sv.Offset=new Vector(0,900);Pump();
Check("external offset wins over residual wheel easing",Math.Abs(sv.Offset.Y-900)<1);
sv.Offset=new Vector(0,500);Wheel(w,-1);Pump(25);Wheel(w,.25);var fine=sv.Offset.Y;Pump();
Check("fractional input cancels the extra easing layer",Math.Abs(sv.Offset.Y-fine)<1);
sv.Offset=new Vector(0,500);Wheel(w,-1);content.Height=100;Pump();
Check("content shrink clamps offset and stops overscroll",sv.Offset.Y>=0&&sv.Offset.Y<=Math.Max(0,sv.Extent.Height-sv.Viewport.Height)+1);
content.Height=3000;Pump();
w.Content=null;Pump();w.Content=sv;Pump();sv.Offset=new Vector(0,400);Wheel(w,-1);Pump();
Check("smoothing still works after detach and reattach",sv.Offset.Y>450);
SmoothScroll.SetEnabled(sv,false);var before=sv.Offset.Y;Wheel(w,-1);Pump();
Check("disabling smoothing preserves platform wheel movement",sv.Offset.Y>before);
w.Close();Pump(10);
var inner=new ScrollViewer{Height=190,Content=new Border{Height=1200,Background=Brushes.White}};
var outer=new ScrollViewer{Content=new StackPanel{Children={inner,new Border{Height=2000,Background=Brushes.White}}}};
SmoothScroll.Attach(outer);SmoothScroll.Attach(inner);var nestedWindow=new Window{Width=400,Height=300,Content=outer};nestedWindow.Show();Pump();Wheel(nestedWindow,-1);Pump();
Check("nested scroll viewer receives wheel without parent stealing it",inner.Offset.Y>0&&outer.Offset.Y==0);
nestedWindow.Close();
Check("all shared icons parse and have finite nonempty bounds",IconCatalog.Names.All(name=>{var b=IconCatalog.Get(name).Bounds;return double.IsFinite(b.Width)&&b.Width>0&&double.IsFinite(b.Height)&&b.Height>0;}));
Check("icons are cached rather than parsed per row",ReferenceEquals(IconCatalog.Get("folder"),IconCatalog.Get("folder")));
Check("unknown icon has a stable file fallback",ReferenceEquals(IconCatalog.Get("unknown"),IconCatalog.Get("file")));

// Reparent the actual overview into a plain headless window. The host MainWindow
// is never shown, so its device initialization and native notification hooks do not run.
var host=new DashFile.Views.MainWindow();
var overview=host.GetControl<Grid>("DevPage");
((Panel)overview.Parent!).Children.Remove(overview);
overview.DataContext=new OverviewSample();
var layoutWindow=new Window{Width=960,Height=500,Content=overview};layoutWindow.Show();Pump();
if (render)
{
    DashFile.Services.ThemeService.ApplyAppearance("light", "m3", "#FFFFFF");
    Pump();
    using var frame = layoutWindow.CaptureRenderedFrame();
    frame?.Save(args[1]);
}
var values=overview.GetVisualDescendants().OfType<TextBlock>()
    .Where(x => Grid.GetColumn(x) == 2 && x.Parent is Grid { Parent: Avalonia.Controls.Primitives.UniformGrid }).ToArray();
Check("original device table shows all 22 parameter values",values.Length==22&&values.All(x=>x.IsEffectivelyVisible&&x.Bounds.Height>0));
Check("device page no longer hides parameters behind expanders",!overview.GetVisualDescendants().OfType<Expander>().Any());
Check("original device page needs no internal scrolling",!overview.GetVisualDescendants().OfType<ScrollViewer>().Any());
var buttons=overview.GetVisualDescendants().OfType<Button>().ToArray();
Check("minimum-width original actions stay inside the page",buttons.All(x=>{var point=x.TranslatePoint(default,overview);return point is { } p&&p.X>=0&&p.Y>=0&&p.X+x.Bounds.Width<=overview.Bounds.Width+1&&p.Y+x.Bounds.Height<=overview.Bounds.Height+1;}));
var rebootButtons = buttons.Where(button => button.Classes.Contains("reboot")).ToArray();
Check("reboot buttons have equal dimensions", rebootButtons.Length == 5 &&
    rebootButtons.All(button => Math.Abs(button.Bounds.Width - rebootButtons[0].Bounds.Width) <= 1 &&
        button.Bounds.Height == rebootButtons[0].Bounds.Height));
Check("reboot labels fit within button padding", rebootButtons.All(button =>
    button.GetVisualDescendants().OfType<TextBlock>().All(label =>
        label.DesiredSize.Width <= button.Bounds.Width - button.Padding.Left - button.Padding.Right)));
var cpu=overview.GetVisualDescendants().OfType<SparkLine>().Single();
Check("original CPU panel retains a visible curve",cpu.Bounds.Height>=46&&cpu.Bounds.Width>200);
Check("parameters stay above the reboot area",values.All(x=>{var point=x.TranslatePoint(default,overview);return point is {} p&&p.Y+x.Bounds.Height<overview.Bounds.Height-179;}));
var menu = overview.ContextMenu!.Items.OfType<MenuItem>().ToArray();
Check("restored page keeps copy and navigation in its context menu", menu.Length == 3);
Check("restored title bar and reboot area retain their seven original buttons", buttons.Length == 7);
DashFile.Services.ThemeService.ApplyAppearance("light","m3","#FFFFFF");
DashFile.Services.ThemeService.ApplyHyprland(true,"m3","#FFFFFF");Pump();
Check("restored layout keeps window dimensions",layoutWindow.Width==960&&layoutWindow.Height==500);
if(render){using var hyprFrame=layoutWindow.CaptureRenderedFrame();hyprFrame?.Save(Path.Combine(Path.GetDirectoryName(args[1])!,"DashFile_Hyprland风格_布局检查.png"));}
Check("Hyprland uses a dark theme and native backdrop",Application.Current!.RequestedThemeVariant==Avalonia.Styling.ThemeVariant.Dark && DashFile.Services.ThemeService.HyprlandEnabled);
Check("white Hyprland accent and interactive surfaces are neutral",
    new[] { "Accent", "AccentHover", "AccentFill", "SelBorder", "BtnPriBg", "TrackOff", "TrackOffHover", "KnobFill" }
        .All(key => Application.Current!.Resources[key] is SolidColorBrush brush &&
            brush.Color.R == brush.Color.G && brush.Color.G == brush.Color.B));
DashFile.Services.ThemeService.ApplyHyprlandTransparency(60);
var backdrop=(SolidColorBrush)Application.Current.Resources["WindowBackdrop"]!;
Check("transparency changes alpha while preserving grey-black color",backdrop.Color.R==32&&backdrop.Color.G==32&&backdrop.Color.B==32&&backdrop.Color.A==102);
DashFile.Services.ThemeService.ApplyHyprlandBackground(Color.FromRgb(48,45,42));
var tinted=(SolidColorBrush)Application.Current.Resources["WindowBackdrop"]!;
Check("picked RGB and transparency are independent",tinted.Color.R==48&&tinted.Color.G==45&&tinted.Color.B==42&&tinted.Color.A==102);
DashFile.Services.ThemeService.ApplyHyprlandBackground(Color.FromRgb(32,32,32));
DashFile.Services.ThemeService.ApplyHyprlandTransparency(double.NaN);
Check("new settings and invalid transparency default to 67 percent",
    new DashFile.Services.AppSettings().HyprlandTransparency == 67 &&
    DashFile.Services.ThemeService.HyprlandTransparency == 67 &&
    ((SolidColorBrush)Application.Current.Resources["WindowBackdrop"]!).Color.A == 84);
DashFile.Services.ThemeService.ApplyHyprlandTransparency(200);
Check("transparency stays inside the supported range",DashFile.Services.ThemeService.HyprlandTransparency==80);
DashFile.Services.ThemeService.ApplyHyprlandTransparency(25);
DashFile.Services.ThemeService.ApplyHyprland(false,"m3","#FFFFFF");
Check("Hyprland toggle restores chosen light appearance",Application.Current!.RequestedThemeVariant==Avalonia.Styling.ThemeVariant.Light);
layoutWindow.Close();
var settings=host.GetControl<ScrollViewer>("SetPage");
var picker=host.GetControl<ColorPicker>("HyprlandBackgroundPicker");
var opacitySlider=host.GetControl<Slider>("HyprlandTransparencySlider");
var materialPanel=host.GetControl<Border>("HyprlandSettingsPanel");
((Panel)settings.Parent!).Children.Remove(settings);settings.DataContext=new OverviewSample();
var settingsWindow=new Window{Width=960,Height=500,Content=settings};settingsWindow.Show();Pump();
Check("color picker uses a circular spectrum with separate alpha",picker.ColorSpectrumShape==ColorSpectrumShape.Ring&&!picker.IsAlphaEnabled);
var pickerY=picker.TranslatePoint(default,materialPanel)!.Value.Y;
var sliderY=opacitySlider.TranslatePoint(default,materialPanel)!.Value.Y;
Check("opacity slider is below color picker without overlap",sliderY>=pickerY+picker.Bounds.Height);
Check("material controls stay inside settings width",opacitySlider.Bounds.Width>0&&opacitySlider.Bounds.Width<=materialPanel.Bounds.Width);
if(render){using var settingsFrame=settingsWindow.CaptureRenderedFrame();settingsFrame?.Save(Path.Combine(Path.GetDirectoryName(args[1])!,"DashFile_独立材质设置_布局检查.png"));}
settingsWindow.Close();

// Use constructor defaults in memory so this check does not save desktop preferences.
var testSettings = DashFile.Services.AppSettings.Current;
testSettings.ThemeDefaultV2 = testSettings.LightMaterialDefaultV4 = true;
testSettings.Appearance = "dark";
testSettings.StyleKey = "m3";
testSettings.MaterialEnabled = true;
testSettings.GlassEnabled = testSettings.HyprlandEnabled = false;
testSettings.HyprlandBackgroundHex = "#202020";
testSettings.HyprlandTransparency = 25;
var appVm = new DashFile.ViewModels.MainViewModel();
Check("startup keeps a saved transparency preference", appVm.HyprlandTransparency == 25);
var baidu = new DashFile.Models.AppItem { Package = "com.baidu", Label = "百度", ApkSize = 200 };
var wechat = new DashFile.Models.AppItem { Package = "com.tencent.mm", Label = "微信", ApkSize = 800 };
var ali = new DashFile.Models.AppItem { Package = "com.eg.android", Label = "支付宝" };
appVm.Apps.ReplaceAll(new[] { wechat, ali, baidu });
appVm.AppSearchText = "  微信 ";
Check("app search matches display name and trims whitespace", appVm.FilteredApps.SequenceEqual(new[] { wechat }));
appVm.AppSearchText = "COM.EG";
Check("app search matches package name ignoring case", appVm.FilteredApps.SequenceEqual(new[] { ali }));
appVm.AppSearchText = "";
Check("Chinese app names sort by pinyin", appVm.FilteredApps.SequenceEqual(new[] { baidu, wechat, ali }));
appVm.AppSortDirection = 1;
Check("name sort can reverse", appVm.FilteredApps.SequenceEqual(new[] { ali, wechat, baidu }));
appVm.AppSortIndex = 1;
Check("descending APK sort places unknown sizes last", appVm.FilteredApps.SequenceEqual(new[] { wechat, baidu, ali }));
appVm.AppSortDirection = 0;
Check("ascending APK sort places unknown sizes last", appVm.FilteredApps.SequenceEqual(new[] { baidu, wechat, ali }));

Console.WriteLine($"{passed} passed, {failed} failed; headless only, no device operations.");
return failed==0?0:1;

public sealed class OverviewSample
{
    public bool IsDeviceTab=>true;
    public bool IsSettingsTab=>true;
    public bool IsHyprlandEnabled=>true;
    public Color HyprlandBackgroundColor=>Color.FromRgb(32,32,32);
    public double HyprlandTransparency=>25;
    public string DevModel=>"OPD2413";
    public string DevStatus=>"已连接";
    public string DevAndroid=>"16";
    public string RootBadge=>"已获取 ROOT";
    public string DevSerial=>"HA2CY7JJ";
    public string DevCodename=>"OP615EL1";
    public string DevLink=>"USB 调试（adb）";
    public string DevScreen=>"1904x3040";
    public string DevBatteryText=>"电量 100%（24.3 ℃）";
    public string DevOs=>"V16.1.0";
    public string DevSlot=>"A 槽位";
    public string DevUnlock=>"已解锁（状态被隐藏模块伪装）";
    public string DevSelinux=>"严格模式（Enforcing）";
    public string DevRing=>"arm64-v8a";
    public string DevCores=>"8 核";
    public string DevCpuModel=>"SM8750P";
    public string DevCpuVendor=>"QTI";
    public string DevCpuName=>"sun";
    public string DevRam=>"10.9 GB";
    public string RamText=>"5.0 / 10.9 GB";
    public double RamPercent=>46;
    public string CpuText=>"1.2 %";
    public IReadOnlyList<double> CpuPoints { get; } = new double[] { 2,3,2,6,4,2,14,4,3,2,5,2,1,4,2,3,2,1 };
    public string DevStorage=>"15G / 211G（已用 8%）";
    public string DevVersion=>"OPD2413_16.0.8.300(CN01)";
    public string DevBuildDate=>"Wed Jun 3 20:17:47 CST 2026";
    public string DevKernel=>"6.6.89-android15-8-g14220ae4ce65-ab13680582-4k";
    public string RootImpl=>"KernelSU";
    public string ManagerVersion=>"v3.3.1";
    public string ManagerMode=>"LKM（模块加载）";
    public string RootHint=>"直接读写 /data/adb/ksu/.allowlist（二进制授权表）";
    public string SuperuserCount=>"2";
    public string ModuleCount=>"2";
    public string DeviceModeText=>"系统模式";
}

