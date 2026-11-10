using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Hearthpulse.App.Main.Meta;

/// <summary>The Standard/Wild community tabs; the page's tab bar picks which one shows (<see cref="ShowTab"/>).</summary>
public partial class ConstructedMetaView : UserControl
{
    /// <summary>
    /// The parts built ahead of the first click, one a dispatcher turn: the four filter bars, the list's card (its head and
    /// column heads, without the rows) and the chosen archetype's card. None has a binding on its own Visibility.
    /// </summary>
    private FrameworkElement[] Parts => [FilterRanks, FilterPeriods, FilterClasses, MinGamesPicker, TierCard, ArchetypeCard];

    /// <summary>Parts built ahead so far; -1 before it started.</summary>
    private int _prebuilt = -1;
    private bool _prebuilding;

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

        // The page is built on the first visit to Standard or Wild: the tab's fixed part is built then, before a click.
        Loaded += (_, _) => Prebuild();
    }

    /// <summary>The model outlives the window: what it keeps holds the dispatcher, never this view.</summary>
    private static Action<Action> Later(Dispatcher dispatcher) => build => _ = dispatcher.BeginInvoke(DispatcherPriority.Background, build);

    /// <summary>
    /// Builds what the meta tabs show whatever the data (the filters, about 280 visuals, and the two cards' frames and
    /// heads) while the page still shows "My stats", so the first click on a meta tab builds the rows alone. The view
    /// stands hidden and zero high in its place (laid out at its real width, taking no room), its parts collapsed, and
    /// one part is shown per dispatcher turn at background priority, so input never waits for more than a part. The
    /// rows are not built ahead: the lists' holder stays collapsed until the tab is really shown. A click in the middle
    /// of it shows everything at once (<see cref="ShowTab"/>). Does nothing when a meta tab is already shown.
    /// </summary>
    public void Prebuild()
    {
        if (_prebuilt >= 0 || Visibility == Visibility.Visible)
        {
            return;
        }

        _prebuilt = 0;
        _prebuilding = true;
        foreach (var part in Parts)
        {
            part.Visibility = Visibility.Collapsed;
        }

        MetaLists.Visibility = Visibility.Collapsed;
        MetaTiersPanel.Visibility = Visibility.Visible;
        Height = 0;
        Visibility = Visibility.Hidden;
        QueuePrebuild();
    }

    private void QueuePrebuild() => _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, PrebuildNext);

    private void PrebuildNext()
    {
        if (!_prebuilding)
        {
            return;
        }

        // Under a hidden page nothing is laid out: carry on when the page shows.
        if (Parent is UIElement { IsVisible: false } page)
        {
            DependencyPropertyChangedEventHandler? shown = null;
            shown = (_, _) =>
            {
                page.IsVisibleChanged -= shown;
                QueuePrebuild();
            };
            page.IsVisibleChanged += shown;
            return;
        }

        if (_prebuilt < Parts.Length)
        {
            var part = Parts[_prebuilt++];
            part.Visibility = Visibility.Visible;
            if (part == ArchetypeCard)
            {
                // Its sections are a turn of their own: the card's frame and name now, the guide and lists next.
                HoldSections();
            }

            QueuePrebuild();
            return;
        }

        // Built: back to an ordinary collapsed view. What was built stays for the tab's first show. The lists' holder
        // stays collapsed until then (ShowTab): shown now, its panel would be measured from the layout queue, under a
        // collapsed view, and build rows nobody sees.
        EndPrebuild();
        MetaTiersPanel.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Collapsed;
    }

    private void EndPrebuild()
    {
        _prebuilding = false;
        _prebuilt = Parts.Length;
        foreach (var part in Parts)
        {
            part.Visibility = Visibility.Visible;
        }

        ClearValue(HeightProperty);
    }

    /// <summary>
    /// Keeps the side card's sections out of the layout pass that is about to run, when they are not built yet, and
    /// lets their binding show them at background priority: the list (or the card's frame) first, the sections after.
    /// </summary>
    private void HoldSections()
    {
        if (ArchetypeSections.ActualHeight > 0 || ArchetypeSections.Visibility != Visibility.Visible)
        {
            return;
        }

        // The current value only: the binding stays and is read again below.
        ArchetypeSections.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ArchetypeSections.GetBindingExpression(VisibilityProperty)?.UpdateTarget());
    }

    /// <summary>A click (or Enter, or Space) on an archetype's row, heard once for both lists: the row is chosen.</summary>
    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ButtonBase { DataContext: MetaArchetypeRow row } && DataContext is MainViewModel model)
        {
            model.SelectArchetype.Execute(row);
        }
    }

    /// <summary>"Tiers", "Top" or "Matchups"; anything else hides the view (the "My stats" tab).</summary>
    public FrameworkElement? ShowTab(string? tab)
    {
        if (_prebuilding)
        {
            EndPrebuild();
        }

        // Shown before it was built ahead: the usual way, all at once.
        _prebuilt = Parts.Length;
        MetaLists.Visibility = Visibility.Visible;
        if (tab == "Tiers")
        {
            HoldSections();
        }

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
