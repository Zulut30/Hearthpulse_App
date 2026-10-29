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
        Assert.Equal(5, AvaMain.NavIndex(AvaMain.NavPages, "History"));
        Assert.Equal(4, AvaMain.NavIndex(AvaMain.BattlegroundsTabs, "Mine"));
        // An unknown name opens the first: Overview, Heroes, Decks.
        Assert.Equal(0, WpfMain.NavIndex(WpfMain.NavPages, "Nowhere"));
        Assert.Equal(0, WpfMain.NavIndex(WpfMain.ConstructedTabs, string.Empty));

        Assert.Equal("Trinkets", WpfMain.NavChecked(WpfMain.BattlegroundsTabs, [false, null, true, false, false]));
        Assert.Equal("Overview", AvaMain.NavChecked(AvaMain.NavPages, [true, false, false, false, false, false, false]));
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
        dynamic model = wpf ? (object)new WpfMain(history, new AppSettings()) : new AvaMain(history, new AppSettings());
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
    public void The_wpf_window_opens_every_page_and_reads_it_back() => OnSta(() =>
    {
        var model = new WpfMain(History(), new AppSettings());
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

    private static void OnSta(Action body)
    {
        Loc.Instance.SetLanguage(Loc.English);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Clear();
                app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve(ThemePalette.DefaultName, 0, 0, 1)));
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("Navigation check failed: " + failure.Message, failure);
        }
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
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
