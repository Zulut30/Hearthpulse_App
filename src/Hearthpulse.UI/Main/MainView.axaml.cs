using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Hearthpulse.Core;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Main;

/// <summary>The dashboard: navigation, the five pages and the floating live bar. Hosted by a window or a mobile shell.</summary>
public partial class MainView : UserControl
{
    private const double CompactWidth = 900;

    private MainViewModel? _viewModel;

    public MainView()
    {
        InitializeComponent();
        foreach (var nav in new[] { NavOverview, NavBattlegrounds, NavConstructed, NavArena, NavHistory, NavSettings })
        {
            nav.IsCheckedChanged += (_, _) => UpdatePages();
        }

        foreach (var tab in new[] { TabHeroes, TabComps, TabTrinkets, TabCards, TabMine })
        {
            tab.IsCheckedChanged += (_, _) => UpdateTabs();
        }

        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
    }

    /// <summary>Raised after any setting changes from the UI.</summary>
    public event Action? SettingsChanged;

    public event Action? ReimportRequested;

    public event Action? ArrangeOverlayRequested;

    /// <summary>Opens the data folder; hosts can replace it (e.g. on mobile, where there is no file manager).</summary>
    public Action<string>? OpenFolder { get; set; } = path =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    private async void OnCopyAccountCode(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { AccountUserCode.Length: > 0 } viewModel)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        try
        {
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(viewModel.AccountUserCode);
            }

            viewModel.AccountCodeCopied(clipboard is not null);
        }
        catch (Exception ex)
        {
            // An async event handler: anything that escaped would reach the crash handler for a failed clipboard copy.
            if (ex is not (InvalidOperationException or System.Runtime.InteropServices.ExternalException))
            {
                Diag.Error("Copying the account code failed", ex);
            }

            viewModel.AccountCodeCopied(false);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.HistoryRequested -= ShowHistoryGame;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.HistoryRequested += ShowHistoryGame;
        SyncOverlaySwitch();
        foreach (var box in this.GetLogicalDescendantsOfType<CheckBox>())
        {
            box.IsCheckedChanged += (_, _) => OnSettingChanged(box);
        }

        foreach (var slider in this.GetLogicalDescendantsOfType<Slider>())
        {
            slider.ValueChanged += (_, _) => SettingsChanged?.Invoke();
        }

        // Segmented choices on the settings page ("Pick suggestions", "Game plan comp"): each radio's Tag names the value
        // it selects, e.g. "PickHints.StatsOnly". The value is applied here, before SettingsChanged, rather than through a
        // two-way binding: Avalonia writes a binding back only after IsCheckedChanged, and a radio group stops writing
        // back once it has unchecked an option. Only the newly checked option reports, so one click applies once.
        foreach (var radio in PageSettings.GetLogicalDescendantsOfType<RadioButton>())
        {
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true && _viewModel is not null && ApplyChoice(_viewModel.Settings, radio.Tag as string))
                {
                    SettingsChanged?.Invoke();
                }
            };
        }
    }

    /// <summary>Applies a settings radio's Tag ("PickHints.Off", "CompMode.Auto"); false when nothing changed.</summary>
    private static bool ApplyChoice(AppSettings settings, string? choice)
    {
        switch (choice?.Split('.'))
        {
            case ["PickHints", var name] when Enum.TryParse<PickHints>(name, out var hints) && settings.PickHints != hints:
                settings.PickHints = hints;
                return true;
            case ["CompMode", var name] when Enum.TryParse<CompMode>(name, out var mode) && settings.CompMode != mode:
                settings.CompMode = mode;
                return true;
            default:
                return false;
        }
    }

    public void ShowPage(string page)
    {
        var nav = page switch
        {
            "Battlegrounds" => NavBattlegrounds,
            "Constructed" => NavConstructed,
            "Arena" => NavArena,
            "History" => NavHistory,
            "Settings" => NavSettings,
            _ => NavOverview,
        };
        nav.IsChecked = true;
    }

    /// <summary>Shows the hover card of one point of the Battlegrounds rating chart (-1 hides it), for the render check.</summary>
    public void HoverBattlegroundsGame(int index) => BgStats.HoverGame(index);

    /// <summary>Shows the hover card of one point of the rank chart (-1 hides it), for the render check.</summary>
    public void HoverConstructedGame(int index) => CtStats.HoverGame(index);

    /// <summary>Shows the hover card of one point of the deck page's win-rate chart (-1 hides it), for the render check.</summary>
    public void HoverDeckGame(int index) => DeckDetails.HoverGame(index);

    public void SyncOverlaySwitch()
    {
        if (_viewModel is not null)
        {
            OverlaySwitch.IsChecked = _viewModel.Settings.OverlayEnabled;
            OverlaySetting.IsChecked = _viewModel.Settings.OverlayEnabled;
        }
    }

    private bool Compact => Classes.Contains("compact");

    private void ApplyWidth(double width)
    {
        var compact = width < CompactWidth;
        Classes.Set("compact", compact);
        UpdateTilePanels();
        Frame.ColumnDefinitions[0].Width = new GridLength(compact ? 76 : 250);
        // Side-by-side cards stack on narrow screens.
        foreach (var (grid, right) in new[] { (HomeCharts, (Control)PlacementCard), (DeckColumns, MatchupCard) })
        {
            grid.ColumnDefinitions = compact ? new ColumnDefinitions("*") : new ColumnDefinitions("3*,16,2*");
            grid.RowDefinitions = compact ? new RowDefinitions("Auto,16,Auto") : new RowDefinitions("Auto");
            Grid.SetColumn(right, compact ? 0 : 2);
            Grid.SetRow(right, compact ? 2 : 0);
        }
    }

    /// <summary>Stat tiles sit in one row on wide screens and two columns on narrow ones (grids classed "fixed" keep theirs).</summary>
    private void UpdateTilePanels()
    {
        foreach (var grid in this.GetVisualDescendants().OfType<UniformGrid>().Where(g => !g.Classes.Contains("fixed")))
        {
            grid.Rows = Compact ? 0 : 1;
            grid.Columns = Compact ? 2 : 0;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(MainViewModel.HearthstoneRunning):
                HsDot.Fill = ThemeBrushes.Get(_viewModel.HearthstoneRunning ? "Positive" : "Faint");
                break;
            case nameof(MainViewModel.MemoryAttached):
                MemDot.Fill = ThemeBrushes.Get(_viewModel.MemoryAttached ? "Positive" : "Faint");
                break;
            case nameof(MainViewModel.InGame):
                LivePulse.Fill = ThemeBrushes.Get(_viewModel.InGame ? "AccentContainer" : "Surface4");
                break;
            case nameof(MainViewModel.DeckOpen):
                UpdatePages();
                PageDeck.Offset = default;
                break;
        }
    }

    private void UpdatePages()
    {
        PageOverview.IsVisible = NavOverview.IsChecked == true;
        PageBattlegrounds.IsVisible = NavBattlegrounds.IsChecked == true;
        // The deck page stands in for the Constructed page while a deck is open (the nav stays on Constructed).
        var deckOpen = _viewModel?.DeckOpen == true;
        PageConstructed.IsVisible = NavConstructed.IsChecked == true && !deckOpen;
        PageDeck.IsVisible = NavConstructed.IsChecked == true && deckOpen;
        PageArena.IsVisible = NavArena.IsChecked == true;
        PageHistory.IsVisible = NavHistory.IsChecked == true;
        PageSettings.IsVisible = NavSettings.IsChecked == true;
        // Panels of a page that was hidden are created on its first layout.
        Dispatcher.UIThread.Post(UpdateTilePanels, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Opens a Battlegrounds tier list tab ("Heroes", "Comps", "Trinkets", "Cards") and scrolls the tabs to the top of the
    /// page (used by the UI render check).
    /// </summary>
    public void ShowBattlegroundsTab(string tab)
    {
        (tab switch
        {
            "Comps" => TabComps,
            "Trinkets" => TabTrinkets,
            "Cards" => TabCards,
            _ => TabHeroes,
        }).IsChecked = true;
        UpdateTabs();
        TabHeroes.BringIntoView(new Rect(0, 0, TabHeroes.Bounds.Width, 700));
    }

    private void UpdateTabs()
    {
        HeroTierCard.IsVisible = TabHeroes.IsChecked == true;
        CompTierCard.IsVisible = TabComps.IsChecked == true;
        TrinketTierCard.IsVisible = TabTrinkets.IsChecked == true;
        CardStatsCard.IsVisible = TabCards.IsChecked == true;
        MyHeroesCard.IsVisible = TabMine.IsChecked == true;
        BracketPicker.IsVisible = TabMine.IsChecked != true;
    }

    private void OnSettingChanged(CheckBox box)
    {
        if (_viewModel is null || box == OverlaySwitch)
        {
            return;
        }

        if (box == OverlaySetting)
        {
            OverlaySwitch.IsChecked = _viewModel.Settings.OverlayEnabled;
        }

        SettingsChanged?.Invoke();
    }

    private void OnViewHistory(object? sender, RoutedEventArgs e) => NavHistory.IsChecked = true;

    /// <summary>Opens History on one game (from a deck page): scrolled to and marked as selected.</summary>
    private void ShowHistoryGame(string key)
    {
        if (_viewModel is null)
        {
            return;
        }

        NavHistory.IsChecked = true;
        var refilter = _viewModel.HistoryFilter != "All";
        if (refilter)
        {
            HistoryAll.IsChecked = true;
            _viewModel.SetHistoryFilter.Execute("All");
        }

        SelectHistoryRow(key, waitForRows: refilter);
    }

    private void SelectHistoryRow(string key, bool waitForRows)
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        if (waitForRows)
        {
            // The "All" rows arrive after a reload: select the game once they are there.
            PropertyChangedEventHandler? handler = null;
            handler = (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.HistoryRows))
                {
                    viewModel.PropertyChanged -= handler;
                    SelectHistoryRow(key, waitForRows: false);
                }
            };
            viewModel.PropertyChanged += handler;
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (viewModel.HistoryRowFor(key) is { } row)
            {
                HistoryList.SelectedItem = row;
                HistoryList.ScrollIntoView(row);
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnOverlaySwitch(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.Settings.OverlayEnabled = OverlaySwitch.IsChecked == true;
        OverlaySetting.IsChecked = _viewModel.Settings.OverlayEnabled;
        SettingsChanged?.Invoke();
    }

    private void OnReimport(object? sender, RoutedEventArgs e) => ReimportRequested?.Invoke();

    private void OnArrangeOverlay(object? sender, RoutedEventArgs e) => ArrangeOverlayRequested?.Invoke();

    private void OnOpenData(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            OpenFolder?.Invoke(_viewModel.DataFolder);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            Core.Diag.Error("Opening the data folder failed", ex);
        }
    }
}

internal static class LogicalTreeHelpers
{
    public static IEnumerable<T> GetLogicalDescendantsOfType<T>(this StyledElement root)
    {
        foreach (var child in ((Avalonia.LogicalTree.ILogical)root).LogicalChildren)
        {
            if (child is T match)
            {
                yield return match;
            }

            if (child is StyledElement element)
            {
                foreach (var nested in element.GetLogicalDescendantsOfType<T>())
                {
                    yield return nested;
                }
            }
        }
    }
}
