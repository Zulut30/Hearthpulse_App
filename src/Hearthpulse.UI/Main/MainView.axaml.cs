using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Main;

/// <summary>The dashboard: navigation, the five pages and the floating live bar. Hosted by a window or a mobile shell.</summary>
public partial class MainView : UserControl
{
    private const double CompactWidth = 900;

    private MainViewModel? _viewModel;

    /// <summary>The page the sidebar went to last (<see cref="MainViewModel.NavPages"/>).</summary>
    private string _page;

    public MainView()
    {
        InitializeComponent();
        _page = CurrentPage;
        var pages = PageButtons;
        for (var i = 0; i < pages.Length; i++)
        {
            var (nav, page) = (pages[i], MainViewModel.NavPages[i]);
            // Avalonia raises this for the newly checked button while the old one is still checked: the page shown is
            // the sender's, and the old button's uncheck that follows only updates what is visible.
            nav.IsCheckedChanged += (_, _) =>
            {
                if (nav.IsChecked == true)
                {
                    _page = page;
                    UpdatePages(page);
                }
                else
                {
                    UpdatePages(null);
                }
            };
        }

        foreach (var tab in new[] { TabHeroes, TabComps, TabTrinkets, TabCards, TabMine })
        {
            tab.IsCheckedChanged += (_, _) => UpdateTabs();
        }

        foreach (var tab in new[] { TabMyDecks, TabMetaTiers, TabMetaDecks, TabMetaMatchups })
        {
            tab.IsCheckedChanged += (_, _) => UpdateConstructedTabs();
        }

        foreach (var tab in SettingsTabs)
        {
            tab.IsCheckedChanged += (_, _) => UpdateSettingsTab();
        }

        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        UseShortcutTips();
    }

    /// <summary>
    /// The overlay's global shortcuts (Ctrl+Shift+H, Ctrl+Shift+L) are registered on Windows only: there the live bar's
    /// tips name them and Settings lists them; elsewhere the short tips stay and the rows are hidden.
    /// </summary>
    private void UseShortcutTips()
    {
        var global = Hearthpulse.UI.Overlay.OverlayView.GlobalShortcuts;
        ShortcutHideRow.IsVisible = global;
        ShortcutLayoutRow.IsVisible = global;
        if (global)
        {
            ArrangeButton.Bind(ToolTip.TipProperty, new LocExtension("Main.Live.ArrangeTip").ProvideValue(null!));
            OverlaySwitch.Bind(ToolTip.TipProperty, new LocExtension("Main.Live.OverlayTip").ProvideValue(null!));
        }
    }

    private RadioButton[] SettingsTabs => [SettingsTabGeneral, SettingsTabOverlay, SettingsTabTracker, SettingsTabAccount, SettingsTabData, SettingsTabAbout];

    /// <summary>Raised after any setting changes from the UI.</summary>
    public event Action? SettingsChanged;

    public event Action? ReimportRequested;

    public event Action? ArrangeOverlayRequested;

    /// <summary>Opens the data folder; hosts can replace it (e.g. on mobile, where there is no file manager).</summary>
    public Action<string>? OpenFolder { get; set; } = path =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = "Event handler: the signature is fixed and the body catches its own errors.")]
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

    /// <summary>The open page, as <see cref="ShowPage"/> takes it.</summary>
    public string CurrentPage => MainViewModel.NavChecked(MainViewModel.NavPages, States(PageButtons)) ?? MainViewModel.NavPages[0];

    /// <summary>The sidebar buttons in <see cref="MainViewModel.NavPages"/> order.</summary>
    private ToggleButton[] PageButtons => [NavOverview, NavBattlegrounds, NavStandard, NavWild, NavArena, NavHistory, NavSettings];

    /// <summary>The Battlegrounds tabs in <see cref="MainViewModel.BattlegroundsTabs"/> order.</summary>
    private ToggleButton[] BattlegroundsTabButtons => [TabHeroes, TabComps, TabTrinkets, TabCards, TabMine];

    /// <summary>The Constructed tabs in <see cref="MainViewModel.ConstructedTabs"/> order.</summary>
    private ToggleButton[] ConstructedTabButtons => [TabMyDecks, TabMetaTiers, TabMetaDecks, TabMetaMatchups];

    private static bool?[] States(ToggleButton[] buttons) => Array.ConvertAll(buttons, b => b.IsChecked);

    // Keys are taken from the window: with nothing focused yet they never reach the view itself.
    private TopLevel? _keyHost;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _keyHost = TopLevel.GetTopLevel(this);
        _keyHost?.AddHandler(KeyDownEvent, OnWindowKeyDown);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keyHost?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        _keyHost = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Ctrl+1…7 open the sidebar pages; Esc and Alt+Left leave a deck or match page (not while typing).</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _viewModel is null)
        {
            return;
        }

        var digit = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D0 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad0 : 0;
        if (e.KeyModifiers == KeyModifiers.Control && MainViewModel.NavPageForDigit(digit) is { } page)
        {
            ShowPage(page);
            e.Handled = true;
        }
        else if (((e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) || (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt))
                 && e.Source is not TextBox)
        {
            e.Handled = _viewModel.GoBack(CurrentPage);
        }
    }

    /// <summary>The mouse's Back button leaves a deck or match page.</summary>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!e.Handled && _viewModel is not null && e.InitialPressMouseButton == MouseButton.XButton1)
        {
            e.Handled = _viewModel.GoBack(CurrentPage);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.HistoryRequested -= ShowHistoryGame;
            _viewModel.ThemeChanged -= OnThemeChanged;
            _viewModel.PanelToggled -= OnPanelToggled;
            StopMenuListeners();
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.HistoryRequested += ShowHistoryGame;
        _viewModel.ThemeChanged += OnThemeChanged;
        _viewModel.PanelToggled += OnPanelToggled;
        BuildPanelsMenu(_viewModel);
        ApplyTheme();
        SyncOverlaySwitch();
        ShowSettingsTab(_viewModel.Settings.SettingsTab);
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

    /// <summary>A theme was picked in Settings: repaint the app in place (brushes are shared and recoloured) and save it.</summary>
    private void OnThemeChanged()
    {
        ApplyTheme();
        SettingsChanged?.Invoke();
    }

    private void ApplyTheme()
    {
        if (_viewModel is null || Application.Current is not { } app)
        {
            return;
        }

        try
        {
            ThemeEngine.Apply(app, _viewModel.CurrentTheme);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diag.Error("Theme could not be applied; keeping the current colours", ex);
        }
    }

    /// <summary>Applies a settings radio's Tag ("PickHints.Off", "Language.ru"); false when nothing changed.</summary>
    private static bool ApplyChoice(AppSettings settings, string? choice)
    {
        switch (choice?.Split('.'))
        {
            case ["PickHints", var name] when Enum.TryParse<PickHints>(name, out var hints) && settings.PickHints != hints:
                settings.PickHints = hints;
                return true;
            case ["Language", var language] when settings.Language != language:
                settings.Language = language;
                Loc.Instance.SetLanguage(settings.InterfaceLanguage());
                return true;
            default:
                return false;
        }
    }

    /// <summary>Shows a Settings tab by its tag ("General", "Overlay", "Tracker", "Account", "Data", "About"); unknown names show General.</summary>
    public void ShowSettingsTab(string tab)
    {
        (SettingsTabs.FirstOrDefault(r => r.Tag as string == tab) ?? SettingsTabGeneral).IsChecked = true;
        UpdateSettingsTab();
    }

    private void UpdateSettingsTab()
    {
        SettingsGeneral.IsVisible = SettingsTabGeneral.IsChecked == true;
        SettingsOverlay.IsVisible = SettingsTabOverlay.IsChecked == true;
        SettingsTracker.IsVisible = SettingsTabTracker.IsChecked == true;
        SettingsAccount.IsVisible = SettingsTabAccount.IsChecked == true;
        SettingsData.IsVisible = SettingsTabData.IsChecked == true;
        SettingsAbout.IsVisible = SettingsTabAbout.IsChecked == true;
        if (_viewModel is not null && SettingsTabs.FirstOrDefault(r => r.IsChecked == true)?.Tag is string tab && _viewModel.Settings.SettingsTab != tab)
        {
            _viewModel.Settings.SettingsTab = tab;
            SettingsChanged?.Invoke();
        }
    }

    /// <summary>Opens a page by name ("Overview", "Battlegrounds", "Standard", "Wild", ...); the old "Constructed" opens Standard.</summary>
    public void ShowPage(string page) => PageButtons[MainViewModel.NavIndex(MainViewModel.NavPages, ConstructedPage.Migrate(page))].IsChecked = true;

    /// <summary>Shows the hover card of one point of the Battlegrounds rating chart (-1 hides it), for the render check.</summary>
    public void HoverBattlegroundsGame(int index) => BgStats.HoverGame(index);

    /// <summary>Shows the hover card of one point of the rank chart (-1 hides it), for the render check.</summary>
    public void HoverConstructedGame(int index) => CtStats.HoverGame(index);

    /// <summary>Shows the hover card of one point of the deck page's win-rate chart (-1 hides it), for the render check.</summary>
    public void HoverDeckGame(int index) => DeckDetails.HoverGame(index);

    /// <summary>The deck page's Versions card (UI render check).</summary>
    public Control DeckVersionsCard => DeckDetails.Versions;

    /// <summary>Opens the ⋯ menu of the deck page's last version row (UI render check).</summary>
    public MenuFlyout? OpenDeckVersionMenu() => DeckDetails.OpenLastVersionMenu();

    public void SyncOverlaySwitch()
    {
        if (_viewModel is not null)
        {
            OverlaySwitch.IsChecked = _viewModel.Settings.OverlayEnabled;
            OverlaySetting.IsChecked = _viewModel.Settings.OverlayEnabled;
            // The overlay's own lock button changes this one too.
            PanelLockSetting.IsChecked = _viewModel.Settings.OverlayPanelsUnlocked;
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

        CtMeta.Stack(compact);
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
            case nameof(MainViewModel.MemoryAttached):
                // One chip, as in WPF: gold while Hearthstone runs, green once its memory is read.
                StatusDot.Fill = ThemeBrushes.Get(_viewModel.MemoryAttached ? "Positive" : _viewModel.HearthstoneRunning ? "Accent" : "Faint");
                break;
            case nameof(MainViewModel.InGame):
                LivePulse.Fill = ThemeBrushes.Get(_viewModel.InGame ? "AccentContainer" : "Surface4");
                break;
            case nameof(MainViewModel.DeckOpen):
                UpdatePages(_page);
                PageDeck.Offset = default;
                break;
            case nameof(MainViewModel.ConstructedFormat):
                // A deck opened on the other format's page moves the nav to its own (a Wild deck to Wild).
                if (ConstructedShown)
                {
                    ShowPage(ConstructedPage.PageOf(_viewModel.ConstructedFormat));
                }

                break;
            case nameof(MainViewModel.MatchOpen):
                // A game opened from the Overview's recent games goes to History, where its page lives.
                if (_viewModel.MatchOpen && NavHistory.IsChecked != true)
                {
                    NavHistory.IsChecked = true;
                }

                UpdatePages(_page);
                PageMatch.Offset = default;
                break;
        }
    }

    /// <summary>True while the Standard or Wild page (or a deck page standing in for it) is open.</summary>
    private bool ConstructedShown => NavStandard.IsChecked == true || NavWild.IsChecked == true;

    /// <summary>Tells the view model the page <paramref name="shown"/> (null: no page change) and shows the checked pages.</summary>
    private void UpdatePages(string? shown)
    {
        _viewModel?.PageShown(shown);

        PageOverview.IsVisible = NavOverview.IsChecked == true;
        PageBattlegrounds.IsVisible = NavBattlegrounds.IsChecked == true;
        // The deck page stands in for the Standard or Wild page while a deck is open (the nav stays on its format).
        var deckOpen = _viewModel?.DeckOpen == true;
        PageConstructed.IsVisible = ConstructedShown && !deckOpen;
        PageDeck.IsVisible = ConstructedShown && deckOpen;
        PageArena.IsVisible = NavArena.IsChecked == true;
        // The match page stands in for the History list while a game is open.
        var matchOpen = _viewModel?.MatchOpen == true;
        PageHistory.IsVisible = NavHistory.IsChecked == true && !matchOpen;
        PageMatch.IsVisible = NavHistory.IsChecked == true && matchOpen;
        PageSettings.IsVisible = NavSettings.IsChecked == true;
        // Panels of a page that was hidden are created on its first layout.
        Dispatcher.UIThread.Post(UpdateTilePanels, DispatcherPriority.Loaded);
    }

    /// <summary>The open Battlegrounds tab ("Heroes", "Comps", "Trinkets", "Cards", "Mine"), kept when the window is rebuilt.</summary>
    public string BattlegroundsTab
    {
        get => MainViewModel.NavChecked(MainViewModel.BattlegroundsTabs, States(BattlegroundsTabButtons)) ?? MainViewModel.BattlegroundsTabs[0];
        set
        {
            BattlegroundsTabButtons[MainViewModel.NavIndex(MainViewModel.BattlegroundsTabs, value)].IsChecked = true;
            UpdateTabs();
        }
    }

    /// <summary>The open Constructed tab ("Decks", "Tiers", "Top", "Matchups"), kept when the window is rebuilt.</summary>
    public string ConstructedTab
    {
        get => MainViewModel.NavChecked(MainViewModel.ConstructedTabs, States(ConstructedTabButtons)) ?? MainViewModel.ConstructedTabs[0];
        set => ShowConstructedTab(value);
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
        BgTiers.ShowTab(BattlegroundsTab);
        BracketPicker.IsVisible = TabMine.IsChecked != true;
    }

    /// <summary>Opens a Constructed tab: "Decks", "Tiers", "Top" or "Matchups" (used by the UI render check).</summary>
    public void ShowConstructedTab(string tab)
    {
        ConstructedTabButtons[MainViewModel.NavIndex(MainViewModel.ConstructedTabs, tab)].IsChecked = true;
        UpdateConstructedTabs();
    }

    private void UpdateConstructedTabs()
    {
        var mine = TabMyDecks.IsChecked == true;
        CtStats.IsVisible = mine;
        DeckColumns.IsVisible = mine;
        var tab = ConstructedTab;
        CtMeta.ShowTab(tab == MainViewModel.ConstructedTabs[0] ? null : tab);
    }

    private void OnSettingChanged(CheckBox box)
    {
        if (_viewModel is null || box == OverlaySwitch || _syncingSettings)
        {
            return;
        }

        // Avalonia writes the switch back to AppSettings only after IsCheckedChanged: the bar re-reads it once it has.
        Dispatcher.UIThread.Post(_viewModel.RefreshPanelToggles);

        if (box == OverlaySetting)
        {
            OverlaySwitch.IsChecked = _viewModel.Settings.OverlayEnabled;
        }

        SettingsChanged?.Invoke();
    }

    private void OnViewHistory(object? sender, RoutedEventArgs e) => NavHistory.IsChecked = true;

    /// <summary>Opens History on one game (from a deck page): scrolled to and marked as selected.</summary>
    private void ShowHistoryGame(string key) => ShowHistoryGameAsync(key).Forget("Show a game in History");

    private async Task ShowHistoryGameAsync(string key)
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        viewModel.CloseMatch();
        NavHistory.IsChecked = true;
        if (viewModel.HistoryFilter != "All")
        {
            HistoryAll.IsChecked = true;
            // Ends once the "All" rows are there: at once from the games in memory, else after a reload.
            await viewModel.ShowAllGamesAsync();
        }

        SelectHistoryRow(viewModel, key);
    }

    private void SelectHistoryRow(MainViewModel viewModel, string key)
    {
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

    private void OnOpenTrackerTab(object? sender, RoutedEventArgs e) => ShowSettingsTab("Tracker");

    private void OnOpenOverlayTab(object? sender, RoutedEventArgs e) => ShowSettingsTab("Overlay");

    private bool _syncingSettings;

    /// <summary>
    /// The live bar's ⋯ menu: every overlay panel, its glyph and name dimmed while it is off. Built here rather than
    /// through an item theme, which would have to name the platform's MenuItem theme.
    /// </summary>
    private void BuildPanelsMenu(MainViewModel viewModel)
    {
        if (PanelsMenuButton.Flyout is not MenuFlyout menu)
        {
            return;
        }

        menu.Items.Clear();
        StopMenuListeners();
        foreach (var toggle in viewModel.PanelToggles)
        {
            var name = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            name.Classes.Add("panelName");
            var glyph = new TextBlock { Text = toggle.Icon, FontSize = 15, Width = 26, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            glyph.Classes.Add("icon");
            var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { glyph, name } };
            header.Classes.Add("panelItem");
            var item = new MenuItem { Header = header, Command = toggle.Toggle, StaysOpenOnClick = true };
            void Show()
            {
                name.Text = toggle.Name;
                header.Classes.Set("off", !toggle.IsOn);
                ToolTip.SetTip(item, toggle.Tip);
            }

            Show();
            PropertyChangedEventHandler listener = (_, _) => Show();
            toggle.PropertyChanged += listener;
            _menuListeners.Add(() => toggle.PropertyChanged -= listener);
            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// The toggles live with the view model: a menu item still listening to them keeps this view (its flyout once opened
    /// is part of the window), and a new data context would add a second listener.
    /// </summary>
    private readonly List<Action> _menuListeners = [];

    private void StopMenuListeners()
    {
        foreach (var stop in _menuListeners)
        {
            stop();
        }

        _menuListeners.Clear();
    }

    /// <summary>A live-bar button switched a panel: the settings page shows it too (its switches bind to AppSettings, which does not notify), then save and apply.</summary>
    private void OnPanelToggled(PanelToggle toggle)
    {
        _syncingSettings = true;
        try
        {
            foreach (var box in PageSettings.GetLogicalDescendants().OfType<ToggleButton>())
            {
                BindingOperations.GetBindingExpressionBase(box, ToggleButton.IsCheckedProperty)?.UpdateTarget();
            }
        }
        finally
        {
            _syncingSettings = false;
        }

        SettingsChanged?.Invoke();
    }

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
