using Avalonia.Controls;
using Avalonia.Markup.Xaml;
namespace FastbootFlashPlugin;
public sealed partial class FlashPage : UserControl
{
    public FlashPage()
    {
        AvaloniaXamlLoader.Load(this);
        // The plugin-management pane is shorter than the main navigation pane.
        // Keep task controls usable there and scroll the whole workspace if necessary.
        var workspace = this.FindControl<Grid>("Workspace")!;
        SizeChanged += (_, _) => workspace.Height = Math.Max(540, Bounds.Height);
    }
}
