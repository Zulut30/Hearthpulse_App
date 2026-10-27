using System.Windows;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Main.Match;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;
using WpfMain = Hearthpulse.App.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The WPF match page resolves its styles (its own, MainLook's, the theme's) only at run time: this lays it out off
/// screen while loading and with a short replay on each tab, so a missing key fails here instead of on the History page.
/// </summary>
public sealed class MatchXamlLoadTests
{
    [Fact]
    public void Wpf_match_page_loads_with_the_theme_on_every_tab()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Check();
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
            throw new InvalidOperationException("Match page XAML failed to load", failure);
        }
    }

    private static void Check()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Tavern", 0, 0, 1)));
        var history = new HistoryStore(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));
        var model = new MatchViewModel(new WpfMain(history, new AppSettings()));
        var view = new MatchView { DataContext = model };

        model.BeginLoading();
        Layout(view);

        var store = new ReplayStore(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N")));
        var game = new GameRecord
        {
            Key = "match-xaml-game",
            StartedAt = new DateTime(2026, 10, 23, 10, 0, 0),
            EndedAt = new DateTime(2026, 10, 23, 10, 7, 0),
            Mode = "Ranked", GameType = "GT_RANKED", Format = "FT_STANDARD", Result = "Loss",
            PlayerClass = "MAGE", OpponentClass = "WARRIOR", DeckName = "Test deck", Turns = 2,
        };
        game = game with
        {
            Replay = store.Save(game.Key, ReplayFile.Write(new ReplayHeader { LocalRank = "Gold 5" },
                ["T 1 L 0", "E 1 L PLAY 0 HERO_08 HERO HEALTH=30", "E 2 O PLAY 0 HERO_01 HERO HEALTH=30",
                 "S MAIN_READY", "V L RESOURCES 1", "B", "E 3 L PLAY 1 CS2_120 MINION ATK=2 HEALTH=3", "P 3 -",
                 "T 2 O 30", "V O RESOURCES 2", "B", "E 4 O PLAY 1 CS2_182 MINION ATK=4 HEALTH=5", "P 4 -", "A 4 3", "V 3 DAMAGE 4",
                 "R LOST"])),
        };
        var page = MatchPage.Load(game, store, CardDb.Empty, null);
        Assert.Null(page.Missing);
        Assert.True(page.Header.Lost);
        model.Show(page);
        foreach (var tab in new[] { "Replay", "Decks", "Analysis" })
        {
            model.SetTab.Execute(tab);
            model.Frame = model.FrameMax;
            Layout(view);
            Assert.True(view.DesiredSize.Height > 0, tab);
        }

        // A game without a replay: the header and the empty state only.
        model.Show(MatchPage.Load(game with { Replay = null }, store, CardDb.Empty, null));
        Layout(view);
        Assert.True(view.DesiredSize.Height > 0);
    }

    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(1180, double.PositiveInfinity));
        view.Arrange(new Rect(0, 0, 1180, view.DesiredSize.Height));
        // Bindings and templates attach at DataBind priority: let them, then lay out again.
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        view.UpdateLayout();
    }
}
