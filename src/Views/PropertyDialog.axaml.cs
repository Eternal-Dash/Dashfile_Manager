using System.Threading.Tasks;
using Avalonia.Controls;
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
    }

    public static async Task ShowAsync(Window owner, PosixEntry e)
    {
        var dlg = new PropertyDialog { Title = "属性 - " + e.Name };
        dlg.GetControlT<TextBlock>("NameText").Text = e.Name;
        dlg.GetControlT<TextBlock>("PathText").Text = e.FullPath;
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
            var val = new TextBlock
            {
                Text = rows[i].Value,
                FontSize = 12.5,
                Foreground = (IBrush?)dlg.FindResource("TextMain") ?? Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            if (rows[i].Mono) val.FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace");

            Grid.SetRow(lbl, i); Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);
            Grid.SetRow(val, i); Grid.SetColumn(val, 1); grid.Children.Add(val);
        }

        await dlg.ShowDialog(owner);
    }

    private T GetControlT<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
