using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using DashFile.Models;

namespace DashFile.Views;

public partial class PropertyDialog : Window
{
    public PropertyDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DashFile.Controls.ViewAnimator.AttachWindow(this);
        _hold.Tick += (_, _) =>
        {
            var field = _heldField;
            StopHold();
            field?.SelectAll();
        };
        Closed += (_, _) => StopHold();
    }

    public static async Task ShowAsync(Window owner, PosixEntry e)
    {
        var dlg = new PropertyDialog { Title = "属性 - " + e.Name };
        dlg.SetCopyable(dlg.GetControlT<SelectableTextBlock>("NameText"), e.Name);
        dlg.SetCopyable(dlg.GetControlT<SelectableTextBlock>("PathText"), e.FullPath);
        dlg.GetControlT<TextBlock>("IconGlyph").Text = e.IsDir ? "📁" : (e.IsLink ? "🔗" : "📄");

        var grid = dlg.GetControlT<Grid>("InfoGrid");
        var rows = new (string Label, string Value, bool Mono)[]
        {
            ("名称",         e.Name,                    false),
            ("目录类型",     e.TypeText + "（" + e.Kind + "）", false),
            ("大小",         e.SizeFull,                false),
            ("修改时间",     e.ModifiedText,            false),
            ("权限",         e.Perms + "  (" + e.ModeOctal + ")", true),
            ("所有者",       e.Owner,                   true),
            ("用户组",       e.Group,                   true),
            ("SELinux 上下文", e.Selinux == "" ? "（无）" : e.Selinux, true),
        };

        grid.RowDefinitions.Clear();
        for (int i = 0; i < rows.Length; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (int i = 0; i < rows.Length; i++)
        {
            var lbl = new TextBlock
            {
                Text = rows[i].Label,
                FontSize = 12,
                Foreground = (IBrush?)dlg.FindResource("TextMuted") ?? Brushes.Gray,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var val = new SelectableTextBlock
            {
                FontSize = 12.5,
                Foreground = (IBrush?)dlg.FindResource("TextMain") ?? Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            if (rows[i].Mono) val.FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace");
            dlg.SetCopyable(val, rows[i].Value);

            Grid.SetRow(lbl, i); Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);
            Grid.SetRow(val, i); Grid.SetColumn(val, 1); grid.Children.Add(val);
        }

        await dlg.ShowDialog(owner);
    }

    private readonly DispatcherTimer _hold = new() { Interval = System.TimeSpan.FromMilliseconds(450) };
    private SelectableTextBlock? _heldField;
    private Point _holdPoint;

    private void SetCopyable(SelectableTextBlock field, string value)
    {
        field.Text = value;
        field.AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (!args.GetCurrentPoint(field).Properties.IsLeftButtonPressed) return;
            _heldField = field;
            _holdPoint = args.GetPosition(field);
            _hold.Stop();
            _hold.Start();
        }, RoutingStrategies.Tunnel);
        field.AddHandler(PointerMovedEvent, (_, args) =>
        {
            var delta = args.GetPosition(field) - _holdPoint;
            if (System.Math.Abs(delta.X) > 4 || System.Math.Abs(delta.Y) > 4) StopHold();
        }, RoutingStrategies.Tunnel);
        field.AddHandler(PointerReleasedEvent, (_, _) => StopHold(), RoutingStrategies.Tunnel);
        field.PointerCaptureLost += (_, _) => StopHold();
        var copy = MenuIcons.Item("复制");
        copy.Click += async (_, _) =>
        {
            if (Clipboard is not { } clipboard) return;
            try
            {
                await clipboard.SetTextAsync(string.IsNullOrEmpty(field.SelectedText) ? value : field.SelectedText);
            }
            catch (System.Exception ex)
            {
                DashFile.Services.AppLog.Add("复制属性失败：" + ex.Message);
            }
        };
        field.ContextMenu = new ContextMenu { Items = { copy } };
    }

    private void StopHold()
    {
        _hold.Stop();
        _heldField = null;
    }

    private T GetControlT<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
