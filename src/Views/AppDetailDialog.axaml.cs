using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DashFile.Views;

public partial class AppDetailDialog : Window
{
    public AppDetailDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DashFile.Controls.ViewAnimator.AttachWindow(this);
    }

    public static async Task ShowAsync(Window owner, string title, string subtitle,
        List<(string Label, string Value)> rows)
    {
        var dlg = new AppDetailDialog { Title = title };
        dlg.FindControl<TextBlock>("TitleText")!.Text = title;
        dlg.FindControl<TextBlock>("SubText")!.Text = subtitle;

        var host = dlg.FindControl<ItemsControl>("RowsHost")!;
        var items = new List<KeyValuePair<string, string>>();
        foreach (var r in rows) items.Add(new KeyValuePair<string, string>(r.Label, r.Value));
        host.ItemsSource = items;

        await dlg.ShowDialog(owner);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
