using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.App.Main;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.HistoryRequested += ShowHistoryGame;
        ShowSettingsTab(viewModel.Settings.SettingsTab);
        SyncStatus();
        StateChanged += (_, _) =>
        {
            var maximized = WindowState == WindowState.Maximized;
            MaxButton.Content = maximized ? "\U000F05B2" : "\U000F05AF";
            // A maximized chrome-less window overhangs the screen by the resize border.
            Frame.Margin = maximized ? new Thickness(7) : new Thickness(0);
        };
    }

    /// <summary>Raised after any setting changes from the UI.</summary>
    public event Action? SettingsChanged;

    public event Action? ReimportRequested;

    public event Action? ArrangeOverlayRequested;

    public void CloseForReal()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>Opens a page by name (see <see cref="CurrentPage"/>); the old "Constructed" opens Standard.</summary>
    public void ShowPage(string page)
    {
        var nav = ConstructedPage.Migrate(page) switch
        {
            "Battlegrounds" => NavBattlegrounds,
            ConstructedPage.Standard => NavStandard,
            ConstructedPage.Wild => NavWild,
            "Arena" => NavArena,
            "History" => NavHistory,
            "Settings" => NavSettings,
            _ => NavOverview,
        };
        nav.IsChecked = true;
    }

    /// <summary>Shows a Settings tab by its tag ("General", "Overlay", "Tracker", "Account", "Data", "About"); unknown names show General.</summary>
    public void ShowSettingsTab(string tab)
    {
        var radio = new[] { SettingsTabOverlay, SettingsTabTracker, SettingsTabAccount, SettingsTabData, SettingsTabAbout }
            .FirstOrDefault(r => (string)r.Tag == tab) ?? SettingsTabGeneral;
        radio.IsChecked = true;
        UpdateSettingsTab(radio);
    }

    public void SyncOverlaySwitch() => OverlaySwitch.IsChecked = _viewModel.Settings.OverlayEnabled;

    /// <summary>The open page, as <see cref="ShowPage"/> takes it.</summary>
    public string CurrentPage =>
        NavBattlegrounds.IsChecked == true ? "Battlegrounds"
        : NavStandard.IsChecked == true ? ConstructedPage.Standard
        : NavWild.IsChecked == true ? ConstructedPage.Wild
        : NavArena.IsChecked == true ? "Arena"
        : NavHistory.IsChecked == true ? "History"
        : NavSettings.IsChecked == true ? "Settings"
        : "Overview";

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.UseDarkChrome(new WindowInteropHelper(this).Handle);
    }

    /// <summary>Ctrl+1…7 open the sidebar pages; Esc and Alt+Left leave a deck or match page (not while typing).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Control
            && MainViewModel.NavPageForDigit(key is >= Key.D1 and <= Key.D9 ? key - Key.D0 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad0 : 0) is { } page)
        {
            ShowPage(page);
            e.Handled = true;
        }
        else if (((key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) || (key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt))
                 && e.OriginalSource is not TextBox)
        {
            e.Handled = _viewModel.GoBack(CurrentPage);
        }
    }

    /// <summary>The mouse's Back button leaves a deck or match page.</summary>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!e.Handled && e.ChangedButton == MouseButton.XButton1)
        {
            e.Handled = _viewModel.GoBack(CurrentPage);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose && _viewModel.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.HistoryRequested -= ShowHistoryGame;
        base.OnClosed(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.HearthstoneRunning):
            case nameof(MainViewModel.MemoryAttached):
            case nameof(MainViewModel.InGame):
                SyncStatus();
                break;
            case nameof(MainViewModel.DeckOpen):
                if (ConstructedShown)
                {
                    OnNavigate(this, new RoutedEventArgs());
                }

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
                else if (NavHistory.IsChecked == true)
                {
                    OnNavigate(this, new RoutedEventArgs());
                }

                break;
        }
    }

    private void SyncStatus()
    {
        StatusDot.Fill = (Brush)FindResource(_viewModel.MemoryAttached ? "Positive" : _viewModel.HearthstoneRunning ? "Accent" : "Faint");
        LivePulse.Fill = (Brush)FindResource(_viewModel.InGame ? "AccentContainer" : "Surface4");
    }

    /// <summary>True while the Standard or Wild page (or a deck page standing in for it) is open.</summary>
    private bool ConstructedShown => NavStandard.IsChecked == true || NavWild.IsChecked == true;

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (PageOverview is null)
        {
            return;
        }

        // Leaving History lets go of the open match: only the match on screen is kept in memory.
        if (_viewModel.MatchOpen && NavHistory.IsChecked != true
            && new[] { NavOverview, NavBattlegrounds, NavStandard, NavWild, NavArena, NavSettings }.Any(n => n.IsChecked == true))
        {
            _viewModel.CloseMatch();
        }

        // Standard and Wild are one page shown for a format.
        if (ConstructedShown)
        {
            _viewModel.ShowConstructedFormat(ConstructedPage.FormatOf(NavWild.IsChecked == true ? ConstructedPage.Wild : ConstructedPage.Standard));
        }

        PageOverview.Visibility = NavOverview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageBattlegrounds.Visibility = NavBattlegrounds.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        // The deck page stands in for the Standard or Wild page while a deck is open (the nav stays on its format).
        PageConstructed.Visibility = ConstructedShown && !_viewModel.DeckOpen ? Visibility.Visible : Visibility.Collapsed;
        PageDeck.Visibility = ConstructedShown && _viewModel.DeckOpen ? Visibility.Visible : Visibility.Collapsed;
        if (PageDeck.Visibility == Visibility.Visible)
        {
            PageDeck.ScrollToTop();
        }

        PageArena.Visibility = NavArena.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        // The match page stands in for the History list while a game is open.
        PageHistory.Visibility = NavHistory.IsChecked == true && !_viewModel.MatchOpen ? Visibility.Visible : Visibility.Collapsed;
        PageMatch.Visibility = NavHistory.IsChecked == true && _viewModel.MatchOpen ? Visibility.Visible : Visibility.Collapsed;
        if (PageMatch.Visibility == Visibility.Visible)
        {
            PageMatch.ScrollToTop();
        }

        PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Reveal(new FrameworkElement[] { PageOverview, PageBattlegrounds, PageConstructed, PageDeck, PageArena, PageHistory, PageMatch, PageSettings }
            .First(p => p.Visibility == Visibility.Visible));
        PlacePill(animate: true);
    }

    private void OnSidebarSize(object sender, SizeChangedEventArgs e) => PlacePill(animate: false);

    /// <summary>Moves the selection pill behind the checked sidebar item, with a light spring when animated.</summary>
    private void PlacePill(bool animate)
    {
        var nav = new[] { NavOverview, NavBattlegrounds, NavStandard, NavWild, NavArena, NavHistory, NavSettings }.FirstOrDefault(n => n.IsChecked == true);
        if (nav is null || Sidebar is null || nav.ActualHeight <= 0)
        {
            return;
        }

        var y = nav.TranslatePoint(new Point(0, 2), Sidebar).Y;
        NavPill.Height = nav.ActualHeight - 4;
        if (!animate || !SystemParameters.ClientAreaAnimation || NavPill.Opacity == 0)
        {
            NavPillShift.BeginAnimation(TranslateTransform.YProperty, null);
            NavPillShift.Y = y;
            NavPill.Opacity = 1;
            return;
        }

        NavPillShift.BeginAnimation(TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(y, TimeSpan.FromMilliseconds(340))
            {
                EasingFunction = new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.25 },
            });
    }

    /// <summary>The page that opens rises a few pixels and fades in; skipped when Windows animations are off.</summary>
    private static void Reveal(FrameworkElement page, double rise = 12)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var shift = new TranslateTransform(0, rise);
        page.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(rise, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
        page.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    private void OnBgTab(object sender, RoutedEventArgs e)
    {
        if (HeroTierCard is null)
        {
            return;
        }

        HeroTierCard.Visibility = TabHeroes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CompTierCard.Visibility = TabComps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TrinketTierCard.Visibility = TabTrinkets.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CardStatsCard.Visibility = TabCards.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MyHeroesCard.Visibility = TabMine.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BracketPicker.Visibility = TabMine.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        if (sender is RadioButton { IsChecked: true })
        {
            Reveal(new FrameworkElement[] { HeroTierCard, CompTierCard, TrinketTierCard, CardStatsCard, MyHeroesCard }.First(c => c.Visibility == Visibility.Visible), 8);
        }
    }

    private void OnCtTab(object sender, RoutedEventArgs e)
    {
        if (MyDecksPanel is null)
        {
            return;
        }

        var meta = TabMyDecks.IsChecked != true;
        MyDecksPanel.Visibility = meta ? Visibility.Collapsed : Visibility.Visible;
        CtStats.Visibility = MyDecksPanel.Visibility;
        MetaTiersPanel.Visibility = TabMetaTiers.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MetaDecksPanel.Visibility = TabMetaDecks.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MetaFilters.Visibility = meta ? Visibility.Visible : Visibility.Collapsed;
        if (sender is RadioButton { IsChecked: true })
        {
            Reveal(new FrameworkElement[] { CtStats, MetaTiersPanel, MetaDecksPanel }.First(c => c.Visibility == Visibility.Visible), 8);
        }
    }

    /// <summary>The match page view (used by the UI render check).</summary>
    public Match.MatchView MatchDetailsPage => MatchDetails;

    /// <summary>The deck page view (used by the UI render check).</summary>
    public Decks.DeckDetailsView DeckDetailsPage => DeckDetails;

    /// <summary>Opens a Constructed tab: "Decks", "Tiers" or "Top" (used by the UI render check).</summary>
    public void ShowConstructedTab(string tab) => (tab switch
    {
        "Tiers" => TabMetaTiers,
        "Top" => TabMetaDecks,
        _ => TabMyDecks,
    }).IsChecked = true;

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
        TabHeroes.BringIntoView(new Rect(0, 0, TabHeroes.ActualWidth, 700));
    }

    private void OnViewHistory(object sender, RoutedEventArgs e) => NavHistory.IsChecked = true;

    /// <summary>Opens History on one game (from a deck page): scrolled to and marked as selected.</summary>
    private void ShowHistoryGame(string key) => ShowHistoryGameAsync(key).Forget("Show a game in History");

    private async Task ShowHistoryGameAsync(string key)
    {
        _viewModel.CloseMatch();
        NavHistory.IsChecked = true;
        if (_viewModel.HistoryFilter != "All")
        {
            HistoryAll.IsChecked = true;
            // Ends once the "All" rows are there: at once from the games in memory, else after a reload.
            await _viewModel.ShowAllGamesAsync();
        }

        SelectHistoryRow(key);
    }

    private void SelectHistoryRow(string key)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (_viewModel.HistoryRowFor(key) is { } row)
            {
                HistoryList.SelectedItem = row;
                HistoryList.ScrollIntoView(row);
            }
        });
    }

    private void OnOverlaySwitch(object sender, RoutedEventArgs e)
    {
        _viewModel.Settings.OverlayEnabled = OverlaySwitch.IsChecked == true;
        SettingsChanged?.Invoke();
    }

    private void OnSettingsTab(object sender, RoutedEventArgs e)
    {
        // A tab is not a setting: stop Checked here so the page's OnSettingChanged does not re-apply every setting. The
        // tab is saved with the next change or on exit.
        e.Handled = true;
        UpdateSettingsTab(sender);
    }

    private void UpdateSettingsTab(object sender)
    {
        // Checked fires while InitializeComponent is still building the page.
        if (SettingsAbout is null || _viewModel is null || sender is not RadioButton { Tag: string tab })
        {
            return;
        }

        _viewModel.Settings.SettingsTab = tab;
        SettingsGeneral.Visibility = SettingsTabGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsOverlay.Visibility = SettingsTabOverlay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsTracker.Visibility = SettingsTabTracker.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsAccount.Visibility = SettingsTabAccount.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsData.Visibility = SettingsTabData.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsAbout.Visibility = SettingsTabAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        // Only what the player changed. A toggle a list builds when its tab is first shown takes its value from a
        // binding before it is loaded (the Tracker tab's counter rows raised 95 Checked at once, each re-applying and
        // saving every setting), and the tab buttons' Unchecked is not a setting either.
        if (IsLoaded && e.OriginalSource is FrameworkElement { IsLoaded: true } source
                     && (source as RadioButton)?.GroupName != SettingsTabGeneral.GroupName)
        {
            Loc.Instance.SetLanguage(_viewModel.Settings.InterfaceLanguage());
            SyncOverlaySwitch();
            SettingsChanged?.Invoke();
        }
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded)
        {
            SettingsChanged?.Invoke();
        }
    }

    private void OnReimport(object sender, RoutedEventArgs e) => ReimportRequested?.Invoke();

    private void OnArrangeOverlay(object sender, RoutedEventArgs e) => ArrangeOverlayRequested?.Invoke();

    private void OnOpenData(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_viewModel.DataFolder}\"") { UseShellExecute = true })?.Dispose();

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
