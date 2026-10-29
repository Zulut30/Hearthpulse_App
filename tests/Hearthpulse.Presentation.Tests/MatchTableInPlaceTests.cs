using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
/// The WPF Battlegrounds table stepping through a recorded game: every list on screen has one row per item and nothing
/// left over, and a row whose item equals the one at its place a step before is still that element. The view model
/// changes its lists in place (stage 10 of docs/perf-architecture-audit.md); a new list each step rebuilt every row.
/// </summary>
public sealed class MatchTableInPlaceTests
{
    /// <summary>
    /// BattlegroundsReplayTests.Game5th as ReplayRecorder writes it (format 4): won on turn 1, lost on turn 2: its events,
    /// then the rounds and lobby.
    /// </summary>
    private const string Game5th = """
        HPR4
        {"kind":"battlegrounds","truncated":false,"events":64}
        E 1 L PLAY 0 TB_BaconShop_HERO_50 HERO HEALTH=30
        E 2 L PLAY 0 TB_BaconShop_HP_050 HERO_POWER
        L 1 L TB_BaconShop_HERO_50 1 30 0 0
        L 4 - BG20_HERO_102 2 30 0 2
        T 1 0
        E 3 B PLAY 1 BGS_004 MINION ATK=1 HEALTH=1 TECH_LEVEL=1
        E 4 B PLAY 2 BGS_039 MINION ATK=2 HEALTH=1 TECH_LEVEL=1
        E 5 B PLAY 3 BG28_810 BATTLEGROUND_SPELL TECH_LEVEL=1
        V L RESOURCES 3
        V L PLAYER_TECH_LEVEL 1
        M BUY 4
        V L RESOURCES_USED 3
        E 4 L HAND 0 BGS_039 MINION ATK=2 HEALTH=1 TECH_LEVEL=1
        Z 4 1
        L 1 L TB_BaconShop_HERO_50 1 30 0 1
        E 6 L PLAY 1 BG26_801 MINION ATK=4 HEALTH=6
        F 1 0
        X 3
        X 5
        E 7 O PLAY 0 BG20_HERO_102 HERO HEALTH=30
        E 8 O PLAY 1 BGS_119 MINION ATK=1 HEALTH=2
        E 9 O PLAY 2 BGS_061 MINION ATK=2 HEALTH=3
        M ATTACK 6 8
        V 8 DAMAGE 2
        V 6 DAMAGE 1
        M DEATHS -
        Y 8
        M ATTACK 9 6
        V 6 DAMAGE 2
        M TRIGGER 6
        V 6 ATK 9
        V 6 HEALTH 7
        M ATTACK 6 9
        V 9 DAMAGE 9
        V 6 DAMAGE 3
        M DEATHS -
        Y 9
        f WIN 0 5
        V 7 DAMAGE 5
        X 7
        T 2 0
        V L RESOURCES 4
        V L RESOURCES_USED 0
        E 10 B PLAY 1 BG21_001 MINION ATK=3 HEALTH=3 TECH_LEVEL=2
        M UPGRADE -
        V L RESOURCES_USED 4
        V L PLAYER_TECH_LEVEL 2
        L 1 L TB_BaconShop_HERO_50 1 30 0 2
        F 2 0
        X 10
        E 11 O PLAY 0 BG20_HERO_102 HERO DAMAGE=5 HEALTH=30
        E 12 O PLAY 1 BGS_119 MINION ATK=6 HEALTH=6
        M ATTACK 12 6
        V 6 DAMAGE 9
        M DEATHS -
        Y 6
        M ATTACK 11 1
        V 1 DAMAGE 30
        L 1 L TB_BaconShop_HERO_50 1 0 0 2
        f LOSS 30 0
        X 11
        X 12
        L 1 L TB_BaconShop_HERO_50 5 0 0 2
        R 5
        G {"rounds":[{"turn":1,"tier":1,"health":30,"board":[{"cardId":"BG26_801","attack":4,"health":6,"golden":false}],"opponentBoard":[{"cardId":"BGS_119","attack":1,"health":2,"golden":false},{"cardId":"BGS_061","attack":2,"health":3,"golden":false}],"shop":["BGS_004","BGS_039","BG28_810"],"damageTaken":0,"damageDealt":0},{"turn":2,"tier":2,"health":30,"board":[{"cardId":"BG26_801","attack":9,"health":4,"golden":false}],"opponentBoard":[{"cardId":"BGS_119","attack":6,"health":6,"golden":false}],"shop":["BG21_001"],"damageTaken":0,"damageDealt":0}],"lobby":[{"heroCardId":"BG20_HERO_102","place":2,"local":false},{"heroCardId":"TB_BaconShop_HERO_50","place":5,"local":true}]}
        """;

    [Fact]
    public void Wpf_battlegrounds_table_keeps_the_rows_that_did_not_change()
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
            throw new InvalidOperationException("The Battlegrounds table check failed", failure);
        }
    }

    private static void Check()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Tavern", 0, 0, 1)));
        var history = new HistoryStore(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));
        var store = new ReplayStore(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N")));
        var game = new GameRecord
        {
            Key = "match-table-bg",
            StartedAt = new DateTime(2026, 10, 23, 10, 0, 0),
            EndedAt = new DateTime(2026, 10, 23, 10, 12, 0),
            Mode = "Battlegrounds", GameType = "GT_BATTLEGROUNDS", Placement = 5, Turns = 2,
        };
        // Lines end in \n in the file, whatever this source file uses.
        game = game with { Replay = store.Save(game.Key, Game5th.ReplaceLineEndings("\n")) };
        var model = new MatchViewModel(new WpfMain(history, new AppSettings()));
        var page = MatchPage.Load(game, store, CardDb.Empty, null);
        Assert.True(page.HasBattlegroundsSteps);
        var view = new MatchView { DataContext = model };
        var window = new Window
        {
            Content = view, Width = 1200, Height = 1400, Left = -20000, Top = -20000, ShowActivated = false,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        try
        {
            model.Show(page);
            var table = (FrameworkElement)view.FindName("BattlegroundsTable");
            var shown = new Dictionary<ItemsControl, (List<object> Items, List<DependencyObject> Rows)>();
            var kept = 0;
            for (var frame = 0; frame <= model.FrameMax; frame++)
            {
                model.GoFrame.Execute(frame);
                Settle(window);
                Assert.True(table.IsVisible);
                var step = model.BattlegroundsStep!;
                var lists = Descendants(table).OfType<ItemsControl>().Where(i => i.IsVisible).ToList();
                Assert.Contains(lists, i => ReferenceEquals(i.ItemsSource, step.Board));
                Assert.Contains(lists, i => ReferenceEquals(i.ItemsSource, model.Leaders));
                foreach (var list in lists)
                {
                    var host = Descendants(list).OfType<Panel>().First(p => p.IsItemsHost);
                    Assert.Equal(list.Items.Count, host.Children.Count);
                    var items = list.Items.Cast<object>().ToList();
                    var rows = Enumerable.Range(0, items.Count).Select(list.ItemContainerGenerator.ContainerFromIndex).ToList();
                    Assert.All(rows.Zip(items), r => Assert.Same(r.Second, Assert.IsAssignableFrom<FrameworkElement>(r.First).DataContext));
                    if (shown.TryGetValue(list, out var before) && before.Items.Count == items.Count)
                    {
                        for (var i = 0; i < items.Count; i++)
                        {
                            if (Equals(before.Items[i], items[i]))
                            {
                                Assert.Same(before.Rows[i], rows[i]);
                                kept++;
                            }
                        }
                    }

                    shown[list] = (items, rows);
                }
            }

            Assert.True(kept > 0, "no row was kept from one step to the next");
        }
        finally
        {
            model.Clear();
            window.Content = null;
            window.Close();
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var below in Descendants(child))
            {
                yield return below;
            }
        }
    }

    /// <summary>Lets bindings, templates and layout run (they attach at DataBind priority and below).</summary>
    private static void Settle(Window window)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }
}
