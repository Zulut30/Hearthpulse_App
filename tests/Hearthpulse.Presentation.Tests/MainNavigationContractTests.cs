using System.Windows;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using WpfWindow = Hearthpulse.App.Main.MainWindow;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The sidebar and tab navigation both main windows share (<c>MainViewModel.Navigation.cs</c>): the page and tab names
/// and their buttons, what showing a page does to the open match and the Constructed format, and the WPF window reading
/// its page back. The Avalonia window's round trip is in Hearthpulse.Tests (LocBindingTests).
/// </summary>
public sealed class MainNavigationContractTests
{
    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    [Fact]
    public void Page_and_tab_names_pick_their_button_and_read_back_from_it_in_both_hosts()
    {
        Assert.Equal(WpfMain.BattlegroundsTabs, AvaMain.BattlegroundsTabs);
        Assert.Equal(WpfMain.ConstructedTabs, AvaMain.ConstructedTabs);
        Assert.Equal(["Heroes", "Comps", "Trinkets", "Cards", "Mine"], WpfMain.BattlegroundsTabs);
        Assert.Equal(["Decks", "Tiers", "Top", "Matchups"], AvaMain.ConstructedTabs);

        Assert.Equal(3, WpfMain.NavIndex(WpfMain.NavPages, ConstructedPage.Wild));
        Assert.Equal(5, AvaMain.NavIndex(AvaMain.NavPages, "Brawl"));
        Assert.Equal(6, AvaMain.NavIndex(AvaMain.NavPages, "History"));
        Assert.Equal(4, AvaMain.NavIndex(AvaMain.BattlegroundsTabs, "Mine"));
        // An unknown name opens the first: Overview, Heroes, Decks.
        Assert.Equal(0, WpfMain.NavIndex(WpfMain.NavPages, "Nowhere"));
        Assert.Equal(0, WpfMain.NavIndex(WpfMain.ConstructedTabs, string.Empty));

        Assert.Equal("Trinkets", WpfMain.NavChecked(WpfMain.BattlegroundsTabs, [false, null, true, false, false]));
        Assert.Equal("Overview", AvaMain.NavChecked(AvaMain.NavPages, [true, false, false, false, false, false, false, false]));
        // Between two buttons of a radio group none is checked.
        Assert.Null(AvaMain.NavChecked(AvaMain.ConstructedTabs, [false, false, null, false]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Showing_a_page_lets_go_of_a_match_left_on_History_and_shows_its_format(bool wpf)
    {
        var history = History();
        Assert.True(history.Add(new GameRecord
        {
            Key = "navigation-game",
            StartedAt = new DateTime(2026, 10, 23, 10, 0, 0),
            EndedAt = new DateTime(2026, 10, 23, 10, 5, 0),
            Mode = "Ranked", GameType = "GT_RANKED", Format = "FT_STANDARD", Result = "Win",
            PlayerClass = "MAGE", OpponentClass = "WARRIOR", DeckName = "First deck",
        }));
        dynamic model = wpf ? (object)new WpfMain(history, new AppSettings(), TestApp.Data) : new AvaMain(history, new AppSettings(), TestApp.Data);
        await model.RefreshAsync();
        Assert.True(await (Task<bool>)model.OpenMatchPageAsync("navigation-game"));

        // On History, and while no page is checked, the match stays.
        model.PageShown("History");
        model.PageShown(null);
        Assert.True((bool)model.MatchOpen);

        model.PageShown(ConstructedPage.Wild);
        Assert.False((bool)model.MatchOpen);
        Assert.Equal("FT_WILD", (string)model.ConstructedFormat);

        model.PageShown(ConstructedPage.Standard);
        Assert.Equal("FT_STANDARD", (string)model.ConstructedFormat);
        model.PageShown("Arena");
        Assert.Equal("FT_STANDARD", (string)model.ConstructedFormat);
    }

    [Fact]
    public void The_wpf_window_opens_every_page_and_reads_it_back() => WpfThread.RunFresh(() =>
    {
        var model = new WpfMain(History(), new AppSettings(), TestApp.Data);
        var window = OffScreen(new WpfWindow(model));
        window.Show();
        Pump();
        try
        {
            foreach (var page in WpfMain.NavPages)
            {
                window.ShowPage(page);
                Pump();
                Assert.Equal(page, window.CurrentPage);
            }

            // The old "Constructed" opens Standard; an unknown page, Overview.
            window.ShowPage("Constructed");
            Assert.Equal(ConstructedPage.Standard, window.CurrentPage);
            window.ShowPage("Nowhere");
            Assert.Equal("Overview", window.CurrentPage);

            window.ShowPage(ConstructedPage.Wild);
            Pump();
            Assert.Equal("FT_WILD", model.ConstructedFormat);

            foreach (var tab in WpfMain.ConstructedTabs)
            {
                window.ConstructedTab = tab;
                Assert.Equal(tab, window.ConstructedTab);
            }

            window.ShowPage("Battlegrounds");
            foreach (var tab in WpfMain.BattlegroundsTabs)
            {
                window.BattlegroundsTab = tab;
                Pump();
                Assert.Equal(tab, window.BattlegroundsTab);
            }
        }
        finally
        {
            // Close() only hides the window to the tray; a window left open keeps its charts on Loc.LanguageChanged.
            window.CloseForReal();
            Pump();
        }
    });

    /// <summary>
    /// Etalon D2: the sidebar items name their Ctrl+1…8 to screen readers, and game rows and History cards show a focus
    /// ring (R13). The Avalonia twin is in LocBindingTests.Keyboard.
    /// </summary>
    [Fact]
    public void The_wpf_sidebar_names_its_shortcuts_and_game_rows_show_focus() => WpfThread.RunFresh(() =>
    {
        var model = new WpfMain(History(), new AppSettings(), TestApp.Data);
        var window = OffScreen(new WpfWindow(model));
        window.Show();
        Pump();
        try
        {
            var items = new[] { "NavOverview", "NavBattlegrounds", "NavStandard", "NavWild", "NavArena", "NavBrawl", "NavHistory", "NavSettings" }
                .Select(n => (DependencyObject)window.FindName(n)).ToList();
            Assert.Equal(WpfMain.NavPages.Count, items.Count);
            Assert.Equal(Enumerable.Range(1, items.Count).Select(i => $"Ctrl+{i}"), items.Select(System.Windows.Automation.AutomationProperties.GetAcceleratorKey));

            Assert.NotNull(((Style)window.FindResource("BareRow")).Setters.OfType<Setter>().Single(s => s.Property == FrameworkElement.FocusVisualStyleProperty).Value);
            var library = window.PageHistory.LibraryView;
            Assert.NotNull(((Style)library.Resources["CardButton"]).Setters.OfType<Setter>().Single(s => s.Property == FrameworkElement.FocusVisualStyleProperty).Value);
        }
        finally
        {
            window.CloseForReal();
            Pump();
        }
    });

    /// <summary>
    /// Review m3: History is one Tab stop; Up and Down move from game to game, Right goes on to the game's own buttons,
    /// Left goes back and Esc returns to the game; Delete asks first. The Avalonia twin is in LocBindingTests.Keyboard.
    /// </summary>
    [Fact]
    public void The_wpf_history_is_one_tab_stop_with_arrows_between_games_and_into_their_buttons() => WpfThread.RunFresh(() =>
    {
        var model = new WpfMain(History(), new AppSettings(), TestApp.Data);
        var window = OffScreen(new WpfWindow(model));
        window.Show();
        window.ShowPage("History");
        Pump();
        try
        {
            var library = window.PageHistory.LibraryView;
            var cards = Enumerable.Range(1, 3)
                .Select(i => new Hearthpulse.App.Main.Match.LibraryCard(Core.Replay.ReplayShelf.Imported, $"{i}.hpr", $"key{i}", battlegrounds: false) { Title = "Game " + i })
                .ToList();
            var list = (System.Windows.Controls.ListBox)library.FindName("List");
            list.ItemsSource = cards;
            Pump();
            var games = cards.Select(c => GameOf(list, c)).ToList();

            // The focus as the window keeps it (its focus scope), not the keyboard's: an off-screen window is not the
            // active one, and another window taking the keyboard during a full run must not move these checks.
            UIElement Focused() => (UIElement)System.Windows.Input.FocusManager.GetFocusedElement(window);
            void FocusOn(UIElement target) => System.Windows.Input.FocusManager.SetFocusedElement(window, target);

            string Stop()
            {
                var focused = Focused() as FrameworkElement;
                return focused?.DataContext is Hearthpulse.App.Main.Match.LibraryCard card && focused is System.Windows.Controls.Button
                    ? $"{cards.IndexOf(card)}:{(games.Contains(focused) ? "game" : System.Windows.Automation.AutomationProperties.GetName(focused))}"
                    : "out";
            }

            string Key(System.Windows.Input.Key key)
            {
                Press(Focused(), key);
                return Stop();
            }

            FocusOn(games[0]);
            var star = "0:" + cards[0].StarTip;
            var share = "0:" + Loc.T("Library.Share");
            var delete = "0:" + Loc.T("Library.Delete");
            Assert.Equal([star, share, delete, delete], new[] { Key(System.Windows.Input.Key.Right), Key(System.Windows.Input.Key.Right), Key(System.Windows.Input.Key.Right), Key(System.Windows.Input.Key.Right) });
            Assert.Equal(share, Key(System.Windows.Input.Key.Left));
            Assert.Equal("0:game", Key(System.Windows.Input.Key.Escape));
            Assert.Equal(["1:game", "2:game", "2:game", "1:game"],
                new[] { Key(System.Windows.Input.Key.Down), Key(System.Windows.Input.Key.Down), Key(System.Windows.Input.Key.Down), Key(System.Windows.Input.Key.Up) });

            // One Tab stop: Tab leaves the list after its first stop, and in a game only the game itself is a stop.
            Assert.Equal(System.Windows.Input.KeyboardNavigationMode.Once, System.Windows.Input.KeyboardNavigation.GetTabNavigation(list));
            Assert.All(cards, c => Assert.Equal([GameOf(list, c)],
                Descendants((DependencyObject)list.ItemContainerGenerator.ContainerFromItem(c)).OfType<System.Windows.Controls.Button>().Where(b => b.IsTabStop)));

            // Delete asks first with the focus on "No"; Esc takes the question back.
            FocusOn(games[2]);
            Key(System.Windows.Input.Key.Delete);
            Assert.True(cards[2].Confirming);
            Assert.Equal(Loc.T("Library.ConfirmNo"), ((System.Windows.Controls.Button)Focused()).Content);
            Assert.Equal("2:game", Key(System.Windows.Input.Key.Escape));
            Assert.False(cards[2].Confirming);
            // Answered with Enter on "No": the question goes, the focus is back on the game.
            Key(System.Windows.Input.Key.Delete);
            Assert.Equal("2:game", Key(System.Windows.Input.Key.Enter));
            Assert.False(cards[2].Confirming);

            // S and O reach the game through the view's keys (no replay file here: the star says so).
            var opened = new List<Uri>();
            model.Library.OpenBrowser = url =>
            {
                opened.Add(url);
                return true;
            };
            cards[2].SiteUrl = "https://hearthpulse.net/replay/abc";
            Key(System.Windows.Input.Key.O);
            Assert.Equal([new Uri("https://hearthpulse.net/replay/abc")], opened);
            Key(System.Windows.Input.Key.S);
            Assert.Equal(Loc.T("Library.Star.Failed"), model.Library.Status);

            // Shift+S shares (no replay file here: it says so). The view's key method takes the modifiers a test cannot press.
            Assert.True(library.ListKey(games[2], System.Windows.Input.Key.S, System.Windows.Input.ModifierKeys.Shift));
            var deadline = DateTime.UtcNow.AddMinutes(1);
            while (model.Library.Status != Loc.T("Library.ShareFailed") && DateTime.UtcNow < deadline)
            {
                Pump();
            }

            Assert.Equal(Loc.T("Library.ShareFailed"), model.Library.Status);
        }
        finally
        {
            window.CloseForReal();
            Pump();
        }
    });

    private static System.Windows.Controls.Button GameOf(System.Windows.Controls.ListBox list, object card)
    {
        var container = (DependencyObject)list.ItemContainerGenerator.ContainerFromItem(card);
        return Descendants(container).OfType<System.Windows.Controls.Button>().First();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var deeper in Descendants(child))
            {
                yield return deeper;
            }
        }
    }

    private static void Press(UIElement target, System.Windows.Input.Key key)
    {
        target.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key)
        {
            RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
        });
        Pump();
    }

    private static WpfWindow OffScreen(WpfWindow window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.Width = 1260;
        window.Height = 840;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        return window;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
