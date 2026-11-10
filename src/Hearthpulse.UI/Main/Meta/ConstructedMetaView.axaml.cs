using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Hearthpulse.Core;

namespace Hearthpulse.UI.Main.Meta;

/// <summary>The Standard/Wild community tabs; the page's tab bar picks which one shows (<see cref="ShowTab"/>).</summary>
public partial class ConstructedMetaView : UserControl
{
    private MainViewModel? _viewModel;

    /// <summary>What is held hidden while the tab is built ahead, with the visibility it had (bound parts read their binding again).</summary>
    private readonly List<(Control Part, bool Was)> _held = [];

    /// <summary>
    /// The turns of the build ahead still to run, the next one first: each shows one held part, or the next few rows of
    /// a held list (and says whether that list has more: then the same turn runs again).
    /// </summary>
    private readonly List<(string Name, Func<bool> Show)> _steps = [];

    /// <summary>
    /// The lists whose rows are being shown a few a turn, each with what stops it: until then a row of theirs is hidden
    /// the moment it is prepared.
    /// </summary>
    private readonly List<(ItemsControl List, Action Stop)> _stagedLists = [];
    private readonly List<(string Step, double Milliseconds)> _turns = [];
    private readonly Stopwatch _clock = new();
    private string? _turn;
    private IDisposable? _prebuildWait;
    private bool _prebuildStarted;
    private bool _prebuilding;

    /// <summary>
    /// How long after the page was shown the build ahead starts (zero: at once). A third of a second: the page has
    /// drawn itself and built its own rows by then, and a player rarely clicks a tab sooner.
    /// </summary>
    public static TimeSpan PrebuildDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The turns of the build ahead that ran, with the time each took from its start to the start of the next (its
    /// layout included): what the UI thread was held for. For the tests.
    /// </summary>
    public IReadOnlyList<(string Step, double Milliseconds)> PrebuildTurns => _turns;

    public ConstructedMetaView()
    {
        InitializeComponent();
        MetaTierList.AddHandler(Button.ClickEvent, OnRowClick);
        MetaTable.AddHandler(Button.ClickEvent, OnRowClick);
    }

    /// <summary>A click (or Enter, or Space) on an archetype's row, heard once for each list: the row is chosen.</summary>
    private void OnRowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (e.Source is Button { DataContext: MetaArchetypeRow row })
        {
            _viewModel?.SelectArchetype.Execute(row);
        }
    }

    /// <summary>
    /// Builds what the meta tabs show whatever the data (the filters, the two cards' frames and heads, the chosen
    /// archetype's sections) while the page still shows "My stats", so the first click on a meta tab builds the rows
    /// alone (the twin of the WPF view's Prebuild). The view is laid out in its place at its real width, but zero high,
    /// transparent, and closed to the pointer and to Tab, with its parts held hidden; one part is shown per dispatcher
    /// turn at background priority: a filter bar, a card's frame, then each block inside the cards, and the rows of a
    /// list inside a part a few a turn, so the UI thread is never held for more than one small part. The tier list's
    /// rows are not built ahead: the lists' holder stays hidden until the
    /// tier list is really shown. A click on a meta tab in the middle of it shows everything at once
    /// (<see cref="ShowTab"/>); leaving the page stops it, and the next visit starts it again. Called by the main view
    /// whenever the Standard or Wild page shows; does nothing once done or while a meta tab is shown.
    /// </summary>
    public void Prebuild()
    {
        if (_prebuildStarted || _prebuildWait is not null || IsVisible)
        {
            return;
        }

        if (PrebuildDelay <= TimeSpan.Zero)
        {
            StartPrebuild();
            return;
        }

        _prebuildWait = DispatcherTimer.RunOnce(
            () =>
            {
                _prebuildWait = null;
                StartPrebuild();
            },
            PrebuildDelay,
            DispatcherPriority.Background);
    }

    private void StartPrebuild()
    {
        // Not under a page that was left meanwhile, and not over a tab that was shown: the next visit asks again.
        if (_prebuildStarted || IsVisible || Parent is not Visual { IsEffectivelyVisible: true })
        {
            return;
        }

        _prebuildStarted = true;
        _prebuilding = true;
        MetaLists.IsVisible = false;
        foreach (var bar in new[] { FilterRanks, FilterPeriods, FilterClasses, MinGamesPicker })
        {
            Stage(bar);
            StageRows(bar.Child as ItemsControl, 4);
        }

        StageCard(TierCard);
        StageCard(ArchetypeCard);
        MetaTiersPanel.IsVisible = true;
        Height = 0;
        Opacity = 0;
        IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        IsVisible = true;
        Dispatcher.UIThread.Post(PrebuildNext, DispatcherPriority.Background);
    }

    /// <summary>Holds <paramref name="part"/> hidden and queues the turn that shows it.</summary>
    private void Stage(Control part)
    {
        _held.Add((part, part.IsVisible));
        // The current value only: a binding on the part's visibility stays and is read again when the part is let go.
        part.SetCurrentValue(IsVisibleProperty, false);
        _steps.Add((part.Name is { Length: > 0 } name ? name : part.GetType().Name, () =>
        {
            Release(part);
            return false;
        }));
    }

    /// <summary>
    /// The rows of a list inside a part just staged: hidden the moment the list prepares them (before it measures them:
    /// a hidden row is not measured and builds nothing, so it costs nothing until its turn), then shown
    /// <paramref name="perTurn"/> a turn in the turns queued here, after the part's own.
    /// </summary>
    private void StageRows(ItemsControl? list, int perTurn)
    {
        if (list is null)
        {
            return;
        }

        var shown = new HashSet<Control>();
        void Hide(object? sender, ContainerPreparedEventArgs e)
        {
            if (!shown.Contains(e.Container))
            {
                e.Container.IsVisible = false;
            }
        }

        void Stop()
        {
            list.ContainerPrepared -= Hide;
            foreach (var row in list.GetRealizedContainers())
            {
                row.IsVisible = true;
            }
        }

        list.ContainerPrepared += Hide;
        _stagedLists.Add((list, Stop));
        _steps.Add(("rows", () =>
        {
            var hidden = list.GetRealizedContainers().Where(row => !shown.Contains(row)).Take(perTurn + 1).ToList();
            foreach (var row in hidden.Take(perTurn))
            {
                shown.Add(row);
                row.IsVisible = true;
            }

            if (hidden.Count > perTurn)
            {
                return true;
            }

            // The last of them: the list's rows are ordinary rows from now on.
            list.ContainerPrepared -= Hide;
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
        foreach (var block in ((Panel)card.Child!).Children.Where(b => b != MetaLists).ToList())
        {
            Stage(block);
            if (block == ArchetypeSections)
            {
                foreach (var section in ArchetypeSections.Children.ToList())
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
    private void Release(Control part)
    {
        var index = _held.FindIndex(h => h.Part == part);
        if (index < 0)
        {
            return;
        }

        var was = _held[index].Was;
        _held.RemoveAt(index);
        if (BindingOperations.GetBindingExpressionBase(part, IsVisibleProperty) is { } binding)
        {
            binding.UpdateTarget();
        }
        else
        {
            part.SetCurrentValue(IsVisibleProperty, was);
        }
    }

    private void PrebuildNext()
    {
        if (_turn is { } ran)
        {
            _turns.Add((ran, _clock.Elapsed.TotalMilliseconds));
            _turn = null;
        }

        if (!_prebuilding)
        {
            return;
        }

        // The page was left: nothing under it is laid out. Start over when it shows again.
        if (Parent is not Visual { IsEffectivelyVisible: true })
        {
            EndPrebuild();
            _prebuildStarted = false;
            MetaTiersPanel.IsVisible = false;
            IsVisible = false;
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

            Dispatcher.UIThread.Post(PrebuildNext, DispatcherPriority.Background);
            return;
        }

        // Built: back to an ordinary hidden view. What was built stays for the tab's first show.
        EndPrebuild();
        MetaTiersPanel.IsVisible = false;
        IsVisible = false;
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
        ClearValue(OpacityProperty);
        ClearValue(IsHitTestVisibleProperty);
        ClearValue(KeyboardNavigation.TabNavigationProperty);
    }

    /// <summary>"Tiers", "Top" or "Matchups"; anything else hides the view (the "My stats" tab).</summary>
    public void ShowTab(string? tab)
    {
        if (tab is not ("Tiers" or "Top" or "Matchups"))
        {
            if (_prebuilding)
            {
                // "My stats" again while the tab is being built ahead under it (a window built anew sets the tab it had):
                // nothing to hide, and the build goes on.
                return;
            }
        }
        else
        {
            _prebuildWait?.Dispose();
            _prebuildWait = null;
            if (_prebuilding)
            {
                EndPrebuild();
            }

            // Shown before it was built ahead: the usual way, all at once.
            _prebuildStarted = true;
        }

        // The lists' holder shows with the tier list only, as in the WPF view.
        MetaLists.IsVisible = tab == "Tiers";
        MetaTiersPanel.IsVisible = tab == "Tiers";
        MetaDecksPanel.IsVisible = tab == "Top";
        MetaMatchupsPanel.IsVisible = tab == "Matchups";
        IsVisible = tab is "Tiers" or "Top" or "Matchups";
    }

    /// <summary>The tier list and the chosen archetype's card: side by side, or one under the other when narrow.</summary>
    public void Stack(bool compact)
    {
        MetaTiersPanel.ColumnDefinitions = compact ? new ColumnDefinitions("*") : new ColumnDefinitions("3*,16,2*");
        MetaTiersPanel.RowDefinitions = compact ? new RowDefinitions("Auto,16,Auto") : new RowDefinitions("Auto");
        Grid.SetColumn(ArchetypeCard, compact ? 0 : 2);
        Grid.SetRow(ArchetypeCard, compact ? 2 : 0);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.DeckCodeCopyRequested -= OnCopyDeck;
            _viewModel.ArchetypeDetailScheduler = null;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.DeckCodeCopyRequested += OnCopyDeck;
            // The side card's sections (mulligan guide, best lists) are built after the list is ready for input.
            _viewModel.ArchetypeDetailScheduler = build => Dispatcher.UIThread.Post(build, DispatcherPriority.Background);
        }
    }

    // Avalonia's clipboard belongs to the top level, not the view model.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = "Handler of the row's copy command (Action); the body catches its own errors.")]
    private async void OnCopyDeck(MetaDeckRow deck)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        try
        {
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(deck.Code);
            }

            _viewModel?.DeckCodeCopied(deck.Name, clipboard is not null);
        }
        catch (Exception ex)
        {
            // An async event handler: anything that escaped would reach the crash handler for a failed clipboard copy.
            if (ex is not (InvalidOperationException or System.Runtime.InteropServices.ExternalException))
            {
                Diag.Error("Copying a deck code failed", ex);
            }

            _viewModel?.DeckCodeCopied(deck.Name, false);
        }
    }
}
