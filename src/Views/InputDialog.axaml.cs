using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DashFile.Views;

public partial class InputDialog : Window
{
    public InputDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DashFile.Controls.ViewAnimator.AttachWindow(this);
    }

    public string? Result { get; private set; }

    public static async Task<string?> ShowAsync(Window owner, string title, string label, string initial = "")
    {
        var dlg = new InputDialog { Title = title };
        dlg.FindControl<TextBlock>("TitleText")!.Text = title;
        dlg.FindControl<TextBlock>("LabelText")!.Text = label;
        var box = dlg.FindControl<TextBox>("Input")!;
        box.Text = initial;
        box.SelectAll();
        box.AttachedToVisualTree += (_, _) => box.Focus();
        var r = await dlg.ShowDialog<string?>(owner);
        return r;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var text = this.FindControl<TextBox>("Input")!.Text ?? "";
        Result = text.Trim();
        Close(Result);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
