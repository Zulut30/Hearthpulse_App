using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Hearthpulse.Core;

namespace Hearthpulse.UI.Main.Meta;

/// <summary>The Standard/Wild community tabs; the page's tab bar picks which one shows (<see cref="ShowTab"/>).</summary>
public partial class ConstructedMetaView : UserControl
{
    private MainViewModel? _viewModel;

    /// <summary>Parts built ahead so far; -1 before it started (and after the page was left in the middle of it).</summary>
    private int _prebuilt = -1;
    private bool _prebuilding;

    /// <summary>
    /// The parts built ahead of the first click, one a dispatcher turn: the four filter bars, the list's card (its head and
    /// column heads, without the rows) and the chosen archetype's card. None has a binding on its own IsVisible.
    /// </summary>
    private Control[] Parts => [FilterRanks, FilterPeriods, FilterClasses, MinGamesPicker, TierCard, ArchetypeCard];

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
    /// Builds what the meta tabs show whatever the data (the filters and the two cards' frames and heads) while the page
    /// still shows "My stats", so the first click on a meta tab builds the rows alone (the twin of the WPF view's
    /// Prebuild). The view is laid out in its place at its real width, but zero high, transparent, and closed to the
    /// pointer and to Tab; its parts are hidden and one is shown per dispatcher turn at background priority, so input
    /// never waits for more than a part. The rows are not built ahead: the lists' holder stays hidden until the tab is
    /// really shown. A click in the middle of it shows everything at once (<see cref="ShowTab"/>). Called by the main
    /// view when the Standard or Wild page shows; does nothing once done or while a meta tab is shown.
    /// </summary>
    public void Prebuild()
    {
        if (_prebuilt >= 0 || IsVisible)
        {
            return;
        }

        _prebuilt = 0;
        _prebuilding = true;
        foreach (var part in Parts)
        {
            part.IsVisible = false;
        }

        MetaLists.IsVisible = false;
        MetaTiersPanel.IsVisible = true;
        Height = 0;
        Opacity = 0;
        IsHitTestVisible = false;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        IsVisible = true;
        Dispatcher.UIThread.Post(PrebuildNext, DispatcherPriority.Background);
    }

    private void PrebuildNext()
    {
        if (!_prebuilding)
        {
            return;
        }

        // The page was left: nothing under it is laid out. Start over when it shows again.
        if (Parent is not Visual { IsEffectivelyVisible: true })
        {
            EndPrebuild();
            _prebuilt = -1;
            MetaTiersPanel.IsVisible = false;
            IsVisible = false;
            return;
        }

        if (_prebuilt < Parts.Length)
        {
            Parts[_prebuilt++].IsVisible = true;
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
        _prebuilt = Parts.Length;
        foreach (var part in Parts)
        {
            part.IsVisible = true;
        }

        MetaLists.IsVisible = true;
        ClearValue(HeightProperty);
        ClearValue(OpacityProperty);
        ClearValue(IsHitTestVisibleProperty);
        ClearValue(KeyboardNavigation.TabNavigationProperty);
    }

    /// <summary>"Tiers", "Top" or "Matchups"; anything else hides the view (the "My stats" tab).</summary>
    public void ShowTab(string? tab)
    {
        if (_prebuilding)
        {
            EndPrebuild();
        }

        // Shown before it was built ahead: the usual way, all at once.
        _prebuilt = Parts.Length;
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
