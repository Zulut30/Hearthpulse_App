using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Match;

namespace Hearthpulse.Tests;

/// <summary>The Avalonia match page's player: its keys on the Replay tab, and the Battlegrounds table on contract records.</summary>
public sealed partial class LocBindingTests
{
    [Fact]
    public void Match_player_keys_reach_the_bar_before_the_focused_control() => Headless.Run(PlayerKeys);

    [Fact]
    public void Match_battlegrounds_table_lays_out_contract_records() => Headless.Run(BattlegroundsTable);

    [Fact]
    public void Match_battlegrounds_table_draws_every_step_of_a_recorded_game() => Headless.Run(RecordedBattlegrounds);

    /// <summary>The match page on the Constructed test game, or on the page <paramref name="load"/> makes from the store.</summary>
    private static (MatchView View, MatchViewModel Model, Window Window, Styles Theme) OpenMatch(string folder, Func<ReplayStore, MatchPage>? load = null)
    {
        var theme = Theme();
        Directory.CreateDirectory(folder);
        var store = new ReplayStore(Path.Combine(folder, "replays"));
        load ??= s => MatchPage.Load(MatchTimelineTests.Game(s.Save(MatchTimelineTests.Game(null).Key, ReplayFile.Write(new ReplayHeader(), MatchTimelineTests.Events))),
            s, CardDb.Empty, null);
        // Playback waits until the test ends: no step is played by the clock here.
        var model = new MatchViewModel(new MainViewModel(new HistoryStore(Path.Combine(folder, "history.db")), new AppSettings(), TestDataFolder.App),
            (_, token) => Task.Delay(Timeout.Infinite, token));
        model.Show(load(store));
        var view = new MatchView { DataContext = model };
        var window = new Window { Width = 1260, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, model, window, theme);
    }

    private static void CloseMatch(MatchViewModel model, Window window, Styles theme, string folder)
    {
        model.Clear();
        window.Close();
        Application.Current!.Styles.Remove(theme);
        Dispatcher.UIThread.RunJobs();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is harmless.
        }
    }

    private static KeyEventArgs Press(Interactive target, Key key, KeyModifiers modifiers = KeyModifiers.None, bool up = false)
    {
        var e = new KeyEventArgs { RoutedEvent = up ? InputElement.KeyUpEvent : InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = target };
        target.RaiseEvent(e);
        return e;
    }

    private static void PlayerKeys()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hp-match-keys-{Guid.NewGuid():N}");
        var (view, model, window, theme) = OpenMatch(folder);
        try
        {
            // Frames of the game: 0 start, 1 mulligan, 2 turn start, 3 play, 4 turn start, 5 play, 6 attack, 7 turn start, 8 hero power, 9 end.
            Assert.True(Press(view, Key.Right).Handled);
            Assert.Equal(1, model.Frame);
            Press(view, Key.Right);
            Assert.Equal(3, model.Frame);
            Press(view, Key.Right, KeyModifiers.Control);
            Assert.Equal(4, model.Frame);
            Press(view, Key.Left, KeyModifiers.Control);
            Assert.Equal(2, model.Frame);
            Press(view, Key.Enter);
            Assert.Equal(3, model.Frame);
            Press(view, Key.Back);
            Assert.Equal(2, model.Frame);
            Press(view, Key.Left);
            Assert.Equal(1, model.Frame);

            Press(view, Key.OemPlus);
            Assert.Equal(2, model.Speed);
            Press(view, Key.Add);
            Assert.Equal(4, model.Speed);
            Press(view, Key.Subtract);
            Assert.Equal(2, model.Speed);
            Press(view, Key.OemMinus);
            Assert.Equal(1, model.Speed);

            // Space on a focused radio of the bar plays the replay; the radio does not take it.
            var theirs = view.GetVisualDescendants().OfType<RadioButton>().Single(r => Equals(r.Content, Core.Localization.Loc.T("Match.View.TheirSide")));
            Assert.True(Press(theirs, Key.Space).Handled);
            Press(theirs, Key.Space, up: true);
            Assert.True(model.Playing);
            Assert.True(model.BottomLocal);
            Press(view, Key.Space);
            Assert.False(model.Playing);

            // The bar's buttons never take the focus; Esc and Alt+Left are not the player's (they go back to History).
            Assert.All(view.GetVisualDescendants().OfType<Button>().Where(b => b.Command == model.TogglePlay || b.Command == model.NextEvent),
                b => Assert.False(b.Focusable));
            Assert.False(Press(view, Key.Escape).Handled);
            Press(view, Key.Left, KeyModifiers.Alt);
            Assert.Equal(1, model.Frame);

            // The timeline is on the bar with the game's stretches.
            var timeline = view.GetVisualDescendants().OfType<MatchTimeline>().Single();
            Assert.Equal(model.TimelineSegments.Count + model.TimelineTicks.Count + 2, timeline.Children.Count);
            Assert.True(timeline.Bounds.Width > 0);

            // Off the Replay tab the keys are not the player's.
            model.ShowDecks = true;
            Assert.False(Press(view, Key.Right).Handled);
            Assert.Equal(1, model.Frame);

            // The window hidden to the tray, or the page hidden in it, stops the playback.
            model.SetTab.Execute("Replay");
            Press(view, Key.Space);
            Assert.True(model.Playing);
            window.Hide();
            Assert.False(model.Playing);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Press(view, Key.Space);
            Assert.True(model.Playing);
            view.IsVisible = false;
            Assert.False(model.Playing);
            Assert.Equal(1, model.Frame);
        }
        finally
        {
            CloseMatch(model, window, theme, folder);
        }
    }

    /// <summary>
    /// The Decks tab draws its rows: their tile takes the theme's Row brush, not the page's own horizontal panel (a local
    /// key of the same name made the tab throw InvalidCastException).
    /// </summary>
    [Fact]
    public void Match_decks_tab_draws_the_deck_rows() => Headless.Run(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hp-match-decks-{Guid.NewGuid():N}");
        var (view, model, window, theme) = OpenMatch(folder);
        try
        {
            model.SetTab.Execute("Decks");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(model.ShowDecks);
            Assert.NotEmpty(model.LocalDeck.Concat(model.OpponentCards));
            var rows = view.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("unusedTile") || p.DataContext is MatchDeckRow).ToList();
            Assert.NotEmpty(rows);
        }
        finally
        {
            CloseMatch(model, window, theme, folder);
        }
    });

    /// <summary>
    /// A Battlegrounds game recorded step by step (won on turn 1, lost on turn 2): the table draws each step, every list
    /// with one row per item and nothing left over, and a row whose item equals the one at its place a step before is
    /// still that control (the lists change in place; a new list each step built every row again).
    /// </summary>
    private static void RecordedBattlegrounds()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hp-match-bg-rec-{Guid.NewGuid():N}");
        var (view, model, window, theme) = OpenMatch(folder, store =>
        {
            var (state, recorder, tracker) = BattlegroundsReplayTests.Play(BattlegroundsReplayTests.Game5th());
            var game = MatchTimelineTests.Game(null) with { Mode = "Battlegrounds", GameType = "GT_BATTLEGROUNDS", Placement = 5 };
            var name = store.Save(game.Key, recorder.Battlegrounds.Text(new ReplayHeader(), tracker.ReplayData(state)));
            return MatchPage.Load(game with { Replay = name }, store, CardDb.Empty, null);
        });
        try
        {
            var table = view.FindControl<Grid>("BattlegroundsTable")!;
            Assert.True(model.ShowBattlegroundsTable && table.IsVisible);
            var timeline = view.GetVisualDescendants().OfType<MatchTimeline>().Single();
            Assert.Equal(model.TimelineSegments.Count + model.TimelineTicks.Count + 2, timeline.Children.Count);

            var shown = new Dictionary<ItemsControl, (List<object> Items, List<Control> Rows)>();
            var kept = 0;
            for (var frame = 0; frame <= model.FrameMax; frame++)
            {
                model.GoFrame.Execute(frame);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var step = model.BattlegroundsStep!;
                Assert.Contains(step.Title, table.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
                var bar = table.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("bgStep"));
                Assert.Equal(step.Phase == MatchPhase.Combat, bar.Classes.Contains("combat"));

                // Every list on screen: one container per item, each showing its item, none left over.
                var lists = table.GetVisualDescendants().OfType<ItemsControl>().Where(i => i.IsEffectivelyVisible && i.ItemsPanelRoot is not null).ToList();
                Assert.Contains(lists, i => ReferenceEquals(i.ItemsSource, step.Board));
                Assert.Contains(lists, i => ReferenceEquals(i.ItemsSource, model.Leaders));
                foreach (var list in lists)
                {
                    var items = list.Items.Cast<object>().ToList();
                    Assert.Equal(items.Count, list.ItemsPanelRoot!.Children.Count);
                    var rows = Enumerable.Range(0, items.Count).Select(i => list.ContainerFromIndex(i)!).ToList();
                    Assert.All(rows.Zip(items), r => Assert.Same(r.Second, r.First.DataContext));
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
            CloseMatch(model, window, theme, folder);
        }
    }

    private static void BattlegroundsTable()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hp-match-bg-{Guid.NewGuid():N}");
        var (view, model, window, theme) = OpenMatch(folder);
        try
        {
            MatchBgCardView Minion(string id, bool attacking = false, bool dying = false) =>
                new(id, "Minion " + id) { Attack = 3, Health = 4, ShowStats = true, Taunt = true, Golden = attacking, Attacking = attacking, Dying = dying, Tooltip = id };
            var step = new MatchBgStepView(12, 40, 5, MatchPhase.Combat, MatchStepKind.Attack, "Turn 5 · combat", "Minion A attacks Minion B", "3 damage")
            {
                Hero = new MatchBgHeroView("TB_BaconShop_HERO_08", "Hagatha", 24, 3, 4)
                {
                    HeroPowers = [new MatchBgCardView("TB_BaconShop_HP_065", "Hero power")],
                    Trinkets = [new MatchBgCardView("BG30_MagicItem_544", "Trinket")],
                },
                Opponent = new MatchBgHeroView("TB_BaconShop_HERO_01", "Bartendotron", 18, 0, 5),
                Board = [Minion("A", attacking: true), Minion("C")],
                OpponentBoard = [Minion("B", dying: true) with { Defending = true }],
                Hand = [new MatchBgCardView("BG_Spell", "Tavern spell")],
                Leaderboard = [new MatchBgLeaderRow("TB_BaconShop_HERO_08", "Hagatha", 2, 24, 3, 4, true, false, false)],
                ResultKey = "Win",
            };

            // The table's part of the page, shown on a step built here (the step itself comes from the format 4 file).
            var table = view.FindControl<Grid>("BattlegroundsTable")!;
            table.IsVisible = true;
            view.FindControl<StackPanel>("BattlegroundsStepCard")!.DataContext = step;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var ovals = table.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("oval")).ToList();
            Assert.Equal(3, ovals.Count);
            Assert.Contains(ovals, o => o.Classes.Contains("attacking") && o.Classes.Contains("golden"));
            Assert.Contains(ovals, o => o.Classes.Contains("defending"));
            Assert.Single(table.GetVisualDescendants().OfType<Panel>(), p => p.Classes.Contains("bgCell") && p.Classes.Contains("dying"));
            var portraits = table.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("portrait")).ToList();
            Assert.Equal(2, portraits.Count);
            Assert.Single(portraits, p => p.Classes.Contains("theirs"));
            var texts = table.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Turn 5 · combat", texts);
            // Health and armor apart (a heart, a shield), never "24 + 3"; the badge's tooltip says both in words.
            Assert.Contains("24", texts);
            Assert.Contains("3", texts);
            Assert.DoesNotContain("24 + 3", texts);
            Assert.Contains(table.GetVisualDescendants().OfType<Border>(), b => Equals(ToolTip.GetTip(b), step.Hero.HealthTip));
            Assert.Equal(Loc.F("Match.Player.HealthArmorTip", 24, 3), step.Hero.HealthTip);
            Assert.Contains("Bartendotron", texts);
            var bar = table.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("bgStep"));
            Assert.Contains("combat", bar.Classes);
            Assert.True(bar.Bounds.Width > 0);

            // The leaderboard rows (from the view model) and the hand's template on contract records.
            var row = Assert.IsAssignableFrom<IDataTemplate>(view.Resources["LeaderRow"]).Build(MatchViewModel.Leader(step.Leaderboard[0]))!;
            row.DataContext = MatchViewModel.Leader(step.Leaderboard[0]);
            window.Content = row;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var rowTexts = row.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("24", rowTexts);
            Assert.Contains("3", rowTexts);
            Assert.DoesNotContain("24 + 3", rowTexts);
            Assert.Contains("leader", row.Classes);
            Assert.DoesNotContain("fighting", row.Classes);

            // The hero choice before the first tavern: round hero portraits with names, no card renders, no Bob's offer
            // and no "No minions" under the empty board.
            window.Content = view;
            var heroes = new MatchChoiceView("Choose your hero",
            [
                new MatchCardView("TB_BaconShop_HERO_08", "Hagatha", false) { Picked = true },
                new MatchCardView("TB_BaconShop_HERO_01", "Bartendotron", false) { Dimmed = true },
            ]) { IsHero = true };
            var choice = new MatchBgStepView(0, 40, 0, MatchPhase.Start, MatchStepKind.Choice, "Start", "You picked Hagatha", string.Empty)
            {
                Hero = new MatchBgHeroView("TB_BaconShop_HERO_08", "Hagatha", 30, 0, 1),
                Choices = [heroes],
            };
            view.FindControl<StackPanel>("BattlegroundsStepCard")!.DataContext = choice;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var shownTexts = table.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.DoesNotContain(Loc.T("Match.Player.Table.EmptyBoard"), shownTexts);
            Assert.DoesNotContain(Loc.T("Match.Player.Table.EmptyShop"), shownTexts);
            Assert.DoesNotContain(Loc.T("Match.Player.Table.BobOffer"), shownTexts);
            var picks = table.GetVisualDescendants().OfType<StackPanel>().Where(s => s.Classes.Contains("heroChoice") && s.IsEffectivelyVisible).ToList();
            Assert.Equal(2, picks.Count);
            Assert.Single(picks, s => s.Classes.Contains("picked"));
            Assert.Single(picks, s => s.Classes.Contains("dimmed"));
            Assert.Contains("Hagatha", shownTexts);
            Assert.DoesNotContain(table.GetVisualDescendants().OfType<Image>(), i => i.IsEffectivelyVisible && i.DataContext is MatchCardView);
        }
        finally
        {
            CloseMatch(model, window, theme, folder);
        }
    }

    /// <summary>
    /// Plan R2 on the Avalonia table: an attack marks its attacker (glow, sword badge) and target (target badge, the
    /// health it lost), the arrow layer finds both and draws a solid arrow; a hero power aimed at a minion marks it with
    /// the dashed frame and a play's arrow; the feed shows every row and the counters say what they count.
    /// </summary>
    [Fact]
    public void Match_constructed_table_marks_the_step_and_draws_its_arrow() => Headless.Run(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hp-match-table-{Guid.NewGuid():N}");
        var (view, model, window, theme) = OpenMatch(folder);
        try
        {
            void Go(ReplayActionKind kind)
            {
                model.GoFrame.Execute(model.FrameOf(kind));
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }

            Panel Mark(int id) => view.GetVisualDescendants().OfType<Panel>().Single(p => p.Classes.Contains("mark") && MatchArrowLayer.GetRef(p) == id && p.IsEffectivelyVisible);
            static bool Shown<T>(Panel mark, string classes) where T : Control =>
                mark.GetVisualDescendants().OfType<T>().Any(c => classes.Split(' ').All(c.Classes.Contains) && c.IsVisible);

            Go(ReplayActionKind.Attack);
            var step = model.Step!;
            Assert.Equal((4, 3, true), (step.ActorRef, step.TargetRef, step.IsAttack));
            var attacker = Mark(4);
            Assert.True(Shown<Avalonia.Controls.Shapes.Ellipse>(attacker, "halo") && Shown<Border>(attacker, "badge attacker") && Shown<Border>(attacker, "frame act"));
            Assert.False(Shown<Border>(attacker, "badge hit"));
            var target = Mark(3);
            Assert.True(Shown<Border>(target, "badge hit") && Shown<Border>(target, "frame hit"));
            Assert.False(Shown<Avalonia.Controls.Shapes.Ellipse>(target, "halo") || Shown<Avalonia.Controls.Shapes.Rectangle>(target, "aim"));
            Assert.Contains("−4", target.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));

            var layer = view.GetVisualDescendants().OfType<MatchArrowLayer>().Single();
            Assert.False(layer.IsHitTestVisible);
            var arrow = layer.Arrow();
            Assert.NotNull(arrow);
            Assert.True(arrow.Attack);
            Assert.False(arrow.Local);
            // The opponent's minion is on top: the arrow goes down to ours.
            Assert.True(arrow.Tip.Y > arrow.Start.Y);

            Go(ReplayActionKind.HeroPower);
            var aimed = Mark(model.Step!.TargetRef);
            Assert.True(Shown<Avalonia.Controls.Shapes.Rectangle>(aimed, "aim") && Shown<Border>(aimed, "badge aim"));
            Assert.False(Shown<Border>(aimed, "badge hit"));
            Assert.True(Shown<Avalonia.Controls.Shapes.Ellipse>(Mark(model.Step.ActorRef), "ring act"));
            Assert.False(layer.Arrow()!.Attack);

            // The feed: a row per entry, the step's marked; the counters' names.
            var feed = view.FindControl<ItemsControl>("Feed")!;
            Assert.Equal(model.Feed.Count, feed.ItemCount);
            Assert.True(view.FindControl<Border>("FeedCard")!.Bounds.Height >= 420);
            var counters = view.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("counter") && p.IsEffectivelyVisible)
                .Select(p => ToolTip.GetTip(p) as string).ToList();
            Assert.Contains(model.Step.Bottom.HandTip, counters);
            Assert.Contains(model.Step.Top.SecretTip, counters);
        }
        finally
        {
            CloseMatch(model, window, theme, folder);
        }
    });
}
