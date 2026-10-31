using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DashFile.Views;

public partial class TextEditorWindow : Window
{
    public TextEditorWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DashFile.Controls.ViewAnimator.AttachWindow(this);
    }

    public static async Task<string?> ShowAsync(Window owner, string path, string content)
    {
        var w = new TextEditorWindow { Title = "编辑 · " + path };
        w.FindControl<TextBlock>("PathText")!.Text = path;
        w.FindControl<TextBox>("Editor")!.Text = content;
        return await w.ShowDialog<string?>(owner);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
        => Close(this.FindControl<TextBox>("Editor")!.Text ?? "");

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
