using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Hearthpulse.App.Main.Meta;

/// <summary>The Standard/Wild community tabs; the page's tab bar picks which one shows (<see cref="ShowTab"/>).</summary>
public partial class ConstructedMetaView : UserControl
{
    /// <summary>What is held collapsed while the tab is built ahead, with the visibility it had (bound parts read their binding again).</summary>
    private readonly List<(FrameworkElement Part, Visibility Was)> _held = [];

    /// <summary>
    /// The turns of the build ahead still to run, the next one first: each shows one held part, or the next few rows of
    /// a held list (and says whether that list has more: then the same turn runs again).
    /// </summary>
    private readonly List<(string Name, Func<bool> Show)> _steps = [];

    /// <summary>
    /// The lists whose rows are being shown a few a turn, each with what stops it: until then a row of theirs is
    /// collapsed the moment it is generated.
    /// </summary>
    private readonly List<(ItemsControl List, Action Stop)> _stagedLists = [];
    private readonly List<(string Step, double Milliseconds)> _turns = [];
    private readonly Stopwatch _clock = new();
    private string? _turn;
    private DispatcherTimer? _prebuildWait;
    private DependencyPropertyChangedEventHandler? _pageShown;
    private bool _prebuildStarted;
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

        // The page is built on the first visit to Standard or Wild: the tab's fixed part is built a moment after it, so
        // the page's own first frame and what it still builds in the background are not held up by it.
        Loaded += (_, _) =>
        {
            if (PrebuildDelay <= TimeSpan.Zero)
            {
                Prebuild();
                return;
            }

            _prebuildWait?.Stop();
            _prebuildWait = new DispatcherTimer(PrebuildDelay, DispatcherPriority.Background, (_, _) => StartNow(), Dispatcher);
            _prebuildWait.Start();

            // The page shown a second time before the wait ran out (the player left it and came back, or the dispatcher
            // never idled long enough for the timer, as in the bench): there is no first frame to protect, so now.
            if (Parent is UIElement page && _pageShown is null)
            {
                _pageShown = (_, e) =>
                {
                    if (e.NewValue is true && !_prebuildStarted)
                    {
                        StartNow();
                    }
                };
                page.IsVisibleChanged += _pageShown;
            }
        };

        // A closed window's view is not kept by a timer that has not fired yet.
        Unloaded += (_, _) => StopWaiting();
    }

    /// <summary>
    /// How long after the page was first shown the build ahead starts (zero: at once). A third of a second: the page
    /// has drawn itself and built its own rows by then, and a player rarely clicks a tab sooner.
    /// </summary>
    public static TimeSpan PrebuildDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The turns of the build ahead that ran, with the time each took from its start to the start of the next (its
    /// layout and drawing included): what the UI thread was held for. For the bench and the tests.
    /// </summary>
    public IReadOnlyList<(string Step, double Milliseconds)> PrebuildTurns => _turns;

    private void StartNow()
    {
        StopWaiting();
        Prebuild();
    }

    private void StopWaiting()
    {
        _prebuildWait?.Stop();
        _prebuildWait = null;
        if (_pageShown is not null && Parent is UIElement page)
        {
            page.IsVisibleChanged -= _pageShown;
        }

        _pageShown = null;
    }

    /// <summary>The model outlives the window: what it keeps holds the dispatcher, never this view.</summary>
    private static Action<Action> Later(Dispatcher dispatcher) => build => _ = dispatcher.BeginInvoke(DispatcherPriority.Background, build);

    /// <summary>
    /// Builds what the meta tabs show whatever the data (the filters, the two cards' frames and heads, the chosen
    /// archetype's sections) while the page still shows "My stats", so the first click on a meta tab builds the rows
    /// alone. The view stands hidden and zero high in its place (laid out at its real width, taking no room) with its
    /// parts held collapsed, and one part is shown per dispatcher turn at background priority: a filter bar, a card's
    /// frame, then each block inside the cards, and the rows of a list inside a part a few a turn (a filter's segments,
    /// the mulligan guide's cards, the best lists), so the UI thread is never held for more than one small part. The
    /// tier list's rows are not built ahead: the lists' holder stays collapsed until the tier list is really shown. A click on a meta tab
    /// in the middle of it shows everything at once (<see cref="ShowTab"/>). Does nothing when it ran before or a
    /// meta tab is already shown.
    /// </summary>
    public void Prebuild()
    {
        if (_prebuildStarted || Visibility == Visibility.Visible)
        {
            return;
        }

        _prebuildStarted = true;
        _prebuilding = true;
        MetaLists.Visibility = Visibility.Collapsed;
        foreach (var bar in new[] { FilterRanks, FilterPeriods, FilterClasses, MinGamesPicker })
        {
            Stage(bar);
            StageRows(bar.Child as ItemsControl, 4);
        }

        StageCard(TierCard);
        StageCard(ArchetypeCard);
        MetaTiersPanel.Visibility = Visibility.Visible;
        Height = 0;
        Visibility = Visibility.Hidden;
        QueuePrebuild();
    }

    /// <summary>Holds <paramref name="part"/> collapsed and queues the turn that shows it.</summary>
    private void Stage(FrameworkElement part)
    {
        _held.Add((part, part.Visibility));
        // The current value only: a binding on the part's visibility stays and is read again when the part is let go.
        part.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        _steps.Add((part.Name is { Length: > 0 } name ? name : part.GetType().Name, () =>
        {
            Release(part);
            return false;
        }));
    }

    /// <summary>
    /// The rows of a list inside a part just staged: collapsed the moment the list generates them (before it measures
    /// them: a collapsed row is not measured and its template is not applied, so it costs nothing until its turn), then
    /// shown <paramref name="perTurn"/> a turn in the turns queued here, after the part's own.
    /// </summary>
    private void StageRows(ItemsControl? list, int perTurn)
    {
        if (list is null)
        {
            return;
        }

        var shown = new HashSet<DependencyObject>();
        var generator = list.ItemContainerGenerator;
        IEnumerable<FrameworkElement> Rows() => Enumerable.Range(0, list.Items.Count).Select(i => generator.ContainerFromIndex(i)).OfType<FrameworkElement>();
        void Hide(object? sender, EventArgs e)
        {
            if (generator.Status == GeneratorStatus.ContainersGenerated)
            {
                foreach (var row in Rows().Where(row => !shown.Contains(row)))
                {
                    row.Visibility = Visibility.Collapsed;
                }
            }
        }

        void Stop()
        {
            generator.StatusChanged -= Hide;
            foreach (var row in Rows())
            {
                row.Visibility = Visibility.Visible;
            }
        }

        generator.StatusChanged += Hide;
        _stagedLists.Add((list, Stop));
        _steps.Add(("rows", () =>
        {
            var hidden = Rows().Where(row => !shown.Contains(row)).Take(perTurn + 1).ToList();
            foreach (var row in hidden.Take(perTurn))
            {
                shown.Add(row);
                row.Visibility = Visibility.Visible;
            }

            if (hidden.Count > perTurn)
            {
                return true;
            }

            // The last of them: the list's rows are ordinary rows from now on.
            generator.StatusChanged -= Hide;
            _stagedLists.RemoveAll(staged => staged.List == list);
            return false;
        }));
    }

    /// <summary>
    /// A card: its frame in one turn, then each block inside it in a turn of its own, and the same one level down for
    /// the side card's sections. The lists' holder is not one of them.
    /// </summary>
    private void StageCard(Border card)
    {
        Stage(card);
        foreach (var block in ((Panel)card.Child).Children.OfType<FrameworkElement>().Where(b => b != MetaLists).ToList())
        {
            Stage(block);
            if (block == ArchetypeSections)
            {
                foreach (var section in ArchetypeSections.Children.OfType<FrameworkElement>().ToList())
                {
                    Stage(section);
                    // The mulligan rank bar's segments, the guide's cards three a turn, the best lists (the heaviest
                    // rows of the tab) one a turn.
                    StageRows(section as ItemsControl ?? (section as Border)?.Child as ItemsControl, section == BestLists ? 1 : section is Border ? 4 : 3);
                }
            }
        }
    }

    /// <summary>Lets a held part show again: as its binding says, or as it was.</summary>
    private void Release(FrameworkElement part)
    {
        var index = _held.FindIndex(h => h.Part == part);
        if (index < 0)
        {
            return;
        }

        var was = _held[index].Was;
        _held.RemoveAt(index);
        if (part.GetBindingExpression(VisibilityProperty) is { } binding)
        {
            binding.UpdateTarget();
        }
        else
        {
            part.SetCurrentValue(VisibilityProperty, was);
        }
    }

    private void QueuePrebuild() => _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, PrebuildNext);

    private void PrebuildNext()
    {
        CloseTurn();
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

        if (_steps.Count > 0)
        {
            var (name, show) = _steps[0];
            _turn = name;
            _clock.Restart();
            if (!show())
            {
                _steps.RemoveAt(0);
            }

            QueuePrebuild();
            return;
        }

        // Built: back to an ordinary collapsed view. What was built stays for the tab's first show. The lists' holder
        // stays collapsed until the tier list is shown (ShowTab): shown now, its panel would be measured from the layout
        // queue, under a collapsed view, and build rows nobody sees.
        EndPrebuild();
        MetaTiersPanel.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>The turn that ran before this one took until now: its own work, its layout and its drawing.</summary>
    private void CloseTurn()
    {
        if (_turn is { } name)
        {
            _turns.Add((name, _clock.Elapsed.TotalMilliseconds));
            _turn = null;
        }
    }

    private void EndPrebuild()
    {
        _prebuilding = false;
        _turn = null;
        _steps.Clear();
        foreach (var (_, stop) in _stagedLists.ToList())
        {
            stop();
        }

        _stagedLists.Clear();

        foreach (var (part, _) in _held.ToList())
        {
            Release(part);
        }

        ClearValue(HeightProperty);
    }

    /// <summary>
    /// Keeps the side card's sections out of the layout pass that is about to run, when they are not built yet, and
    /// lets their binding show them at background priority: the list first, the sections after.
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
        if (tab is not ("Tiers" or "Top" or "Matchups"))
        {
            if (_prebuilding)
            {
                // "My stats" again while the tab is being built ahead under it: nothing to hide, and the build goes on.
                return null;
            }
        }
        else
        {
            if (_prebuilding)
            {
                EndPrebuild();
            }

            // Shown before it was built ahead: the usual way, all at once.
            _prebuildStarted = true;
        }

        // The lists' holder shows with the tier list only: under another meta tab its panel would be measured from
        // the layout queue and build rows nobody sees.
        MetaLists.Visibility = tab == "Tiers" ? Visibility.Visible : Visibility.Collapsed;
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
