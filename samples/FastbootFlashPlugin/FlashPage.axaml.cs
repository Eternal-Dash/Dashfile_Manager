using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
namespace FastbootFlashPlugin;
public sealed partial class FlashPage : UserControl
{
    private FlashViewModel? _session;

    public FlashPage()
    {
        AvaloniaXamlLoader.Load(this);
        var viewport = this.FindControl<ScrollViewer>("Viewport")!;
        var workspace = this.FindControl<Grid>("Workspace")!;
        // Size only from the parent's viewport. Never feed DesiredSize/MinHeight back into layout.
        viewport.PropertyChanged += (_, e) =>
        {
            if (e.Property != ScrollViewer.ViewportProperty) return;
            var height = Math.Max(560, viewport.Viewport.Height);
            if (Math.Abs(workspace.Height - height) > 0.5) workspace.Height = height;
        };
        AttachedToVisualTree += (_, _) => Observe(DataContext as FlashViewModel);
        DetachedFromVisualTree += (_, _) => Observe(null);
        DataContextChanged += (_, _) => { if (this.GetVisualRoot() != null) Observe(DataContext as FlashViewModel); };
    }

    private void Observe(FlashViewModel? session)
    {
        if (_session != null) _session.PropertyChanged -= OnSessionChanged;
        _session = session;
        if (_session != null) _session.PropertyChanged += OnSessionChanged;
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        var section = e.PropertyName switch
        {
            nameof(FlashViewModel.HasPlan) when _session?.HasPlan == true => "PlanSection",
            nameof(FlashViewModel.IsLogOpen) when _session?.IsLogOpen == true => "LogSection",
            _ => null
        };
        if (section == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            var target = this.FindControl<Expander>(section);
            if (IsEffectivelyVisible && target?.IsEffectivelyVisible == true) target.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
}
