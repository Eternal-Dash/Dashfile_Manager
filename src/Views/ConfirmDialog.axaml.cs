using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DashFile.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DashFile.Controls.ViewAnimator.AttachWindow(this);
    }

    public static async Task<bool> ShowAsync(Window owner, string title, string message)
    {
        var dlg = new ConfirmDialog { Title = title };
        dlg.GetControl<TextBlock>("TitleText").Text = title;
        dlg.GetControl<TextBlock>("MessageText").Text = message;
        return await dlg.ShowDialog<bool>(owner);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
