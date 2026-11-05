using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Main.Match;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;
using WpfMain = Hearthpulse.App.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The WPF match page resolves its styles (its own, MainLook's, the theme's) only at run time: this lays it out off
/// screen while loading and with a short replay on each tab, so a missing key fails here instead of on the History page;
/// the Battlegrounds table is laid out on contract records built here (the steps come from a format 4 file).
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
        var model = new MatchViewModel(new WpfMain(history, new AppSettings(), TestApp.Data));
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

        // The player: the timeline over the bar, a child per stretch and tick plus the position line and diamond.
        model.SetTab.Execute("Replay");
        Layout(view);
        var timeline = Descendants(view).OfType<MatchTimeline>().Single();
        Assert.True(timeline.ActualWidth > 0);
        Assert.Equal(model.TimelineSegments.Count + model.TimelineTicks.Count + 2, timeline.Children.Count);
        Assert.Equal(model.TimelineSegments.Count, model.Turns.Count);

        // Plan R2: the attack's arrow is found between the two minions (it never takes the mouse); the attacker shows its
        // sword badge, the target its health lost; the feed lists every row beside a table as tall as it.
        model.Frame = model.FrameOf(ReplayActionKind.Attack);
        Layout(view);
        var layer = Descendants(view).OfType<MatchArrowLayer>().Single();
        Assert.False(layer.IsHitTestVisible);
        Assert.True(layer.Arrow() is { Attack: true, Local: false });
        Assert.Contains(Descendants(view).OfType<Border>(), b => b is { Name: "Badge", Visibility: Visibility.Visible, DataContext: MatchCardView { Acting: true } });
        Assert.Contains(Descendants(view).OfType<TextBlock>(), t => t.Text == "−4");
        var feed = (ItemsControl)view.FindName("Feed");
        Assert.Equal(model.Feed.Count, feed.Items.Count);
        Assert.True(((FrameworkElement)view.FindName("FeedCard")).ActualHeight >= 420);
        var counters = Descendants(view).OfType<StackPanel>().Select(p => p.ToolTip as string).ToList();
        Assert.Contains(model.Step!.Bottom.HandTip, counters);

        // The heroes' health by a heart and armor by a shield (never "30 + 5"), the badge named in words.
        foreach (var side in new[] { model.Step.Top, model.Step.Bottom })
        {
            Assert.Contains(Descendants(view).OfType<Border>(), b => Equals(b.ToolTip, side.HealthTip) && AutomationProperties.GetName(b) == side.HealthTip);
            Assert.DoesNotContain(Descendants(view).OfType<TextBlock>(), t => t.Text == $"{side.Health} + {side.Armor}");
        }

        // Plan R1: the "Turn" list over the timeline shows the stretch on screen; a bookmark adds its mark under them.
        var turns = (ComboBox)view.FindName("TurnBox");
        Assert.Equal(model.Jumps.Count, turns.Items.Count);
        Assert.Equal(model.JumpIndex, turns.SelectedIndex);
        model.SetReplay("match-xaml.hpr", null, history);
        model.ToggleBookmark.Execute(null);
        Layout(view);
        Assert.Equal(model.TimelineSegments.Count + model.TimelineTicks.Count + 1 + 2, timeline.Children.Count);
        Assert.False(model.ShowHeaderActions);
        model.ToggleBookmark.Execute(null);

        // The window hidden to the tray stops the playback (the page's IsVisible goes with the window's).
        var window = new Window
        {
            Content = new MatchView { DataContext = model }, Width = 1200, Height = 800, Left = -20000, Top = -20000, ShowActivated = false,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        model.TogglePlay.Execute(null);
        Assert.True(model.Playing);
        window.Hide();
        Assert.False(model.Playing);
        window.Content = null;
        window.Close();

        Battlegrounds(view);

        // A game without a replay: the header and the empty state only.
        model.Show(MatchPage.Load(game with { Replay = null }, store, CardDb.Empty, null));
        Layout(view);
        Assert.True(view.DesiredSize.Height > 0);
    }

    /// <summary>The Battlegrounds table on a tavern step and a combat step, and a leaderboard row.</summary>
    private static void Battlegrounds(MatchView view)
    {
        MatchBgCardView Minion(string id) => new(id, "Minion " + id) { Attack = 3, Health = 4, ShowStats = true, Tooltip = id };
        var hero = new MatchBgHeroView("TB_BaconShop_HERO_08", "Hagatha", 24, 3, 4)
        {
            HeroPowers = [new MatchBgCardView("TB_BaconShop_HP_065", "Hero power")],
            Trinkets = [new MatchBgCardView("BG30_MagicItem_544", "Trinket")],
        };
        var tavern = new MatchBgStepView(3, 40, 2, MatchPhase.Shop, MatchStepKind.Buy, "Turn 2 · tavern", "You bought Minion A", string.Empty)
        {
            Hero = hero,
            Board = [Minion("A") with { Acting = true, Golden = true, Taunt = true }],
            Shop = [Minion("B") with { Frozen = true, Tier = 2 }, Minion("C") with { Tier = 1, DivineShield = true, Reborn = true }],
            Hand = [new MatchBgCardView("BG_Spell", "Tavern spell")],
            Choices = [new MatchChoiceView("Discover", [new MatchCardView("BG_X", "Option", false) { Picked = true }])],
            Gold = "7 / 10",
            ShopFrozen = true,
            Leaderboard = [new MatchBgLeaderRow("TB_BaconShop_HERO_08", "Hagatha", 2, 24, 3, 4, true, false, false)],
        };
        var combat = tavern with
        {
            Phase = MatchPhase.Combat,
            Kind = MatchStepKind.Attack,
            Title = "Turn 2 · combat",
            Opponent = new MatchBgHeroView("TB_BaconShop_HERO_01", "Bartendotron", 18, 0, 5),
            Board = [Minion("A") with { Attacking = true }, Minion("D")],
            OpponentBoard = [Minion("E") with { Defending = true, Dying = true, Venomous = true }],
            Shop = [],
            Choices = [],
            Gold = string.Empty,
            ResultKey = "Win",
        };

        var table = (FrameworkElement)view.FindName("BattlegroundsTable");
        var card = (FrameworkElement)view.FindName("BattlegroundsStepCard");
        table.Visibility = Visibility.Visible;
        foreach (var (step, ovals) in new[] { (tavern, 3), (combat, 3) })
        {
            card.DataContext = step;
            Layout(view);
            Assert.True(card.ActualHeight > 0, step.Title);
            Assert.Equal(ovals, Descendants(card).OfType<Border>().Count(b => b.Name == "Oval"));
            Assert.Contains(Descendants(card).OfType<TextBlock>(), t => t.Text == step.Title);
        }

        // Duos: while the partner's table fights, the bottom shows their board and hero under the caption, not ours.
        var partnerPanel = (FrameworkElement)view.FindName("BattlegroundsPartner");
        Assert.Equal(Visibility.Collapsed, partnerPanel.Visibility);
        var duos = combat with
        {
            PartnerBoard = [Minion("P") with { Attacking = true }, Minion("Q")],
        };
        card.DataContext = duos;
        Layout(view);
        Assert.Equal(Visibility.Visible, partnerPanel.Visibility);
        bool Shown(DependencyObject element)
        {
            for (var at = element; at is not null && !ReferenceEquals(at, card); at = VisualTreeHelper.GetParent(at))
            {
                if (at is UIElement { Visibility: not Visibility.Visible })
                {
                    return false;
                }
            }

            return true;
        }

        Assert.Equal(3, Descendants(card).OfType<Border>().Count(b => b.Name == "Oval" && Shown(b)));
        var shownTexts = Descendants(card).OfType<TextBlock>().Where(Shown).Select(t => t.Text).ToList();
        Assert.Contains(Loc.T("Match.Player.Table.Partner"), shownTexts);
        Assert.DoesNotContain("Hagatha", shownTexts);

        // The hero choice before the first tavern: hero portraits with names on top, no Bob's offer, no "No minions".
        var start = tavern with
        {
            Index = 0,
            Phase = MatchPhase.Start,
            Kind = MatchStepKind.Choice,
            Title = "Start",
            Board = [],
            Shop = [],
            Gold = string.Empty,
            Choices =
            [
                new MatchChoiceView("Choose your hero",
                [
                    new MatchCardView("TB_BaconShop_HERO_08", "Hagatha", false) { Picked = true },
                    new MatchCardView("TB_BaconShop_HERO_01", "Bartendotron", false) { Dimmed = true },
                ]) { IsHero = true },
            ],
        };
        card.DataContext = start;
        Layout(view);
        var startTexts = Descendants(card).OfType<TextBlock>().Where(Shown).Select(t => t.Text).ToList();
        Assert.DoesNotContain(Loc.T("Match.Player.Table.EmptyBoard"), startTexts);
        Assert.DoesNotContain(Loc.T("Match.Player.Table.EmptyShop"), startTexts);
        Assert.DoesNotContain(Loc.T("Match.Player.Table.BobOffer"), startTexts);
        Assert.Equal(2, Descendants(card).OfType<TextBlock>().Count(t => t.Name == "NameText" && Shown(t)));
        Assert.Single(Descendants(card).OfType<System.Windows.Shapes.Ellipse>(), e => e.Name == "Ring" && Shown(e));
        // The pick is named as picked for a screen reader, the other option by its name.
        var names = Descendants(card).OfType<StackPanel>().Where(p => p.Name == "Root" && Shown(p)).Select(AutomationProperties.GetName).ToList();
        Assert.Equal([Loc.F("Match.Player.HeroPicked", "Hagatha"), "Bartendotron"], names);

        var row = (FrameworkElement)((DataTemplate)view.Resources["LeaderRow"]).LoadContent();
        row.DataContext = Hearthpulse.App.Main.Match.MatchViewModel.Leader(tavern.Leaderboard[0]);
        Layout(row);
        Assert.True(row.DesiredSize.Height > 0);
        // Health and armor apart (the armor by a shield), never "24 + 3"
        Assert.Contains(Descendants(row).OfType<TextBlock>(), t => t.Text == "24");
        Assert.Contains(Descendants(row).OfType<TextBlock>(), t => t.Text == "3");
        Assert.DoesNotContain(Descendants(row).OfType<TextBlock>(), t => t.Text == "24 + 3");
        var tip = Loc.F("Match.Player.HealthArmorTip", 24, 3);
        Assert.Contains(Descendants(row).OfType<StackPanel>(), p => Equals(p.ToolTip, tip) && AutomationProperties.GetName(p) == tip);
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

    private static void Layout(FrameworkElement view)
    {
        view.Measure(new Size(1180, double.PositiveInfinity));
        view.Arrange(new Rect(0, 0, 1180, view.DesiredSize.Height));
        // Bindings and templates attach at DataBind priority: let them, then lay out again.
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        view.UpdateLayout();
    }
}
