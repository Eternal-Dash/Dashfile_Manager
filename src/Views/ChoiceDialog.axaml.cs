using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DashFile.Views;

/// <summary>二选一弹窗，返回 0=A、1=B、null=关闭</summary>
public partial class ChoiceDialog : Window
{
    private int? _result;

    public ChoiceDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DashFile.Controls.ViewAnimator.AttachWindow(this);
    }

    public static async Task<int?> ShowAsync(Window owner, string title, string message,
        string optionA, string optionB)
    {
        var dlg = new ChoiceDialog();
        dlg.FindControl<TextBlock>("TitleText")!.Text = title;
        dlg.FindControl<TextBlock>("MessageText")!.Text = message;
        dlg.FindControl<Button>("BtnA")!.Content = optionA;
        dlg.FindControl<Button>("BtnB")!.Content = optionB;
        await dlg.ShowDialog(owner);
        return dlg._result;
    }

    private void OnA(object? sender, RoutedEventArgs e) { _result = 0; Close(); }
    private void OnB(object? sender, RoutedEventArgs e) { _result = 1; Close(); }
}
