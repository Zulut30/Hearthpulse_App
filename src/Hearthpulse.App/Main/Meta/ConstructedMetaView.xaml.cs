using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Hearthpulse.App.Main.Meta;

/// <summary>The Standard/Wild community tabs; the page's tab bar picks which one shows (<see cref="ShowTab"/>).</summary>
public partial class ConstructedMetaView : UserControl
{
    public ConstructedMetaView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old)
            {
                old.ArchetypeDetailScheduler = null;
            }

            if (e.NewValue is MainViewModel model)
            {
                // The side card's sections (mulligan guide, best lists) are built after the list is ready for input:
                // background priority is below input and, unlike idle, is not held off by the overlay's timer in a game.
                model.ArchetypeDetailScheduler = Later(Dispatcher);
            }
        };
    }

    /// <summary>The model outlives the window: what it keeps holds the dispatcher, never this view.</summary>
    private static Action<Action> Later(Dispatcher dispatcher) => build => _ = dispatcher.BeginInvoke(DispatcherPriority.Background, build);

    /// <summary>"Tiers", "Top" or "Matchups"; anything else hides the view (the "My stats" tab).</summary>
    public FrameworkElement? ShowTab(string? tab)
    {
        MetaTiersPanel.Visibility = tab == "Tiers" ? Visibility.Visible : Visibility.Collapsed;
        MetaDecksPanel.Visibility = tab == "Top" ? Visibility.Visible : Visibility.Collapsed;
        MetaMatchupsPanel.Visibility = tab == "Matchups" ? Visibility.Visible : Visibility.Collapsed;
        Visibility = tab is "Tiers" or "Top" or "Matchups" ? Visibility.Visible : Visibility.Collapsed;
        return tab switch
        {
            "Tiers" => MetaTiersPanel,
            "Top" => MetaDecksPanel,
            "Matchups" => MetaMatchupsPanel,
            _ => null,
        };
    }
}
