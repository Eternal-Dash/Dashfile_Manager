using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace FastbootFlashPlugin;

internal enum FlashSection { Plan, Log }

public sealed partial class FlashPage : UserControl
{
    private readonly Grid _accordion;
    private readonly Expander[] _sections;
    private bool _updating;
    private FlashViewModel? _session;

    public FlashPage()
    {
        AvaloniaXamlLoader.Load(this);
        _accordion = this.FindControl<Grid>("Accordion")!;
        _sections = _accordion.Children.OfType<Expander>().ToArray();
        foreach (var section in _sections)
            section.PropertyChanged += (_, change) =>
            {
                if (change.Property == Expander.IsExpandedProperty && !_updating)
                    ArrangeSections(section.IsExpanded ? section : null);
            };
        ArrangeSections(_sections[0]);
        AttachedToVisualTree += (_, _) => Observe(DataContext as FlashViewModel);
        DetachedFromVisualTree += (_, _) => Observe(null);
        DataContextChanged += (_, _) =>
        {
            if (this.GetVisualRoot() != null) Observe(DataContext as FlashViewModel);
        };
    }

    private void ArrangeSections(Expander? selected)
    {
        _updating = true;
        try
        {
            for (var index = 0; index < _sections.Length; index++)
            {
                var expanded = ReferenceEquals(_sections[index], selected);
                _sections[index].IsExpanded = expanded;
                var fill = expanded && _sections[index].Name is "PlanSection" or "LogSection";
                _accordion.RowDefinitions[index].Height = fill ? GridLength.Star : GridLength.Auto;
            }
            var fillsAvailableSpace = selected?.Name is "PlanSection" or "LogSection";
            _accordion.RowDefinitions[^1].Height = fillsAvailableSpace ? new GridLength(0) : GridLength.Star;
        }
        finally { _updating = false; }
    }

    private void Observe(FlashViewModel? session)
    {
        if (_session != null) _session.SectionRequested -= OpenSection;
        _session = session;
        if (_session != null) _session.SectionRequested += OpenSection;
    }

    private void OpenSection(FlashSection section)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsEffectivelyVisible) return;
            var name = section == FlashSection.Plan ? "PlanSection" : "LogSection";
            ArrangeSections(this.FindControl<Expander>(name));
        }, DispatcherPriority.Loaded);
    }
}
