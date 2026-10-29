using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
using WpfOverlay = Hearthpulse.App.Overlay.OverlayViewModel;
using AvaOverlay = Hearthpulse.UI.Overlay.OverlayViewModel;
using WpfGeometry = Hearthpulse.App.Overlay.LeaderboardGeometry;
using AvaGeometry = Hearthpulse.UI.Overlay.LeaderboardGeometry;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The badges beside the Battlegrounds leaderboard tiles ("bg-badges"): tavern tier and main tribe from the lobby rows,
/// placed on the calibrated tiles, in both compiled overlays.
/// </summary>
public sealed class BgBadgesTests
{
    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
        [
          {"id":"BG_MURLOC","dbfId":21,"name":"Murloc","type":"MINION","races":["MURLOC"]},
          {"id":"BG_BEAST","dbfId":22,"name":"Beast","type":"MINION","races":["BEAST"]},
          {"id":"BG_DRAGON","dbfId":23,"name":"Dragon","type":"MINION","races":["DRAGON"]},
          {"id":"BG_AMALGAM","dbfId":24,"name":"Amalgam","type":"MINION","races":["ALL"]},
          {"id":"BG_NEUTRAL","dbfId":25,"name":"Neutral","type":"MINION"},
          {"id":"HERO_ME","dbfId":26,"name":"Me","type":"HERO"}
        ]
        """)));

    private static BgMinion M(string cardId) => new(cardId, cardId, 2, 2, Golden: false, TechLevel: 1, Keywords: []);

    private static BattlegroundsView Lobby(IReadOnlyList<BgOpponent> opponents, bool duos = false, bool heroSelection = false) =>
        new(7, 3, 6, "HERO_ME", 20, 0, 2, duos, false, heroSelection, [], ["DRAGON", "BEAST", "MURLOC"], true, opponents, [], [], [M("BG_BEAST")], null, 0, [], []);

    private static LiveSnapshot Live(BattlegroundsView view) => LiveSnapshot.Empty with { Battlegrounds = view, InGame = true, HearthstoneRunning = true };

    private static readonly BgOpponent Murlocs = new(2, "HERO_A", "Alice", 20, 0, 4, 1, false, 0,
        [M("BG_MURLOC"), M("BG_MURLOC"), M("BG_MURLOC"), M("BG_AMALGAM"), M("BG_BEAST"), M("BG_BEAST")], 6, "Win", 3, false);

    private static readonly BgOpponent Tie = new(3, "HERO_B", "Bob", 15, 0, 3, 3, false, 0, [M("BG_BEAST"), M("BG_DRAGON"), M("BG_NEUTRAL")], 5, "Loss", 2, false);

    private static readonly BgOpponent Unseen = new(4, "HERO_C", "Cid", 30, 0, 2, 4, false, 0, [], 0, null, 0, false);

    private static readonly BgOpponent Amalgams = new(6, "HERO_E", "Eve", 12, 0, 5, 5, false, 0, [M("BG_AMALGAM"), M("BG_NEUTRAL")], 4, "Win", 1, false);

    private static readonly BgOpponent Dead = new(5, "HERO_D", "Dee", 0, 0, 5, 8, true, 0, [M("BG_DRAGON"), M("BG_DRAGON")], 4, "Win", 9, false);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Badges_say_what_the_lobby_rows_say_for_every_opponent_and_skip_the_player(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        dynamic model = wpf ? new WpfOverlay() : new AvaOverlay();
        var settings = new AppSettings();
        model.ArrangeLeaderboard(1920.0, 1080.0, settings);
        model.Update(Live(Lobby([Murlocs, Tie, Unseen, Amalgams, Dead])), Cards, settings, null, null);

        Assert.True((bool)model.ShowBgBadges);
        var badges = ((IEnumerable<dynamic>)model.BgBadges).ToList();
        // The player is second on the leaderboard and has no badge of their own.
        Assert.Equal([1, 3, 4, 5, 8], badges.Select(b => (int)b.Place));
        Assert.Equal([4, 3, 2, 5, 5], badges.Select(b => (int)b.Tier));

        // One count for the lobby panels and the badges: every badge repeats its opponent's lobby row.
        var rows = ((IEnumerable<dynamic>)model.LobbyRows).Where(r => !(bool)r.You).ToDictionary(r => (int)r.Place);
        foreach (var badge in badges)
        {
            var row = rows[(int)badge.Place];
            Assert.Equal((string?)row.Tribe?.Key, (string?)badge.Tribe?.Key);
            Assert.Equal((string?)row.Tribe?.Glyph, (string?)badge.Tribe?.Glyph);
            Assert.Equal((int)row.TribeCount, (int)badge.TribeCount);
            Assert.Equal((int)row.Tier, (int)badge.Tier);
            Assert.Equal((bool)row.Dead, (bool)badge.Dead);
        }

        // Three murlocs and the amalgam; a tie goes to the lobby's first tribe; nothing before a fight or with amalgams only.
        Assert.Equal(("MURLOC", 4), ((string)badges[0].Tribe.Key, (int)badges[0].TribeCount));
        Assert.Equal(("DRAGON", 1), ((string)badges[1].Tribe.Key, (int)badges[1].TribeCount));
        Assert.Null((object?)badges[2].Tribe);
        Assert.Null((object?)badges[3].Tribe);
        Assert.True((bool)badges[4].Dead);
        Assert.Equal(("DRAGON", 2), ((string)badges[4].Tribe.Key, (int)badges[4].TribeCount));

        // The hover popup names the same tribe.
        model.HoverLeaderboard(1, 100.0, 200.0);
        Assert.Equal($"Murloc ×4", (string)model.Lobby.Tribe);
        Assert.Equal((string)badges[0].Tribe.Glyph, (string)model.Lobby.TribeGlyph);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Badges_follow_a_fight_and_a_death_on_the_next_snapshot(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        dynamic model = wpf ? new WpfOverlay() : new AvaOverlay();
        var settings = new AppSettings();
        model.ArrangeLeaderboard(1920.0, 1080.0, settings);
        model.Update(Live(Lobby([Unseen])), Cards, settings, null, null);
        Assert.Null((object?)((IEnumerable<dynamic>)model.BgBadges).Single().Tribe);

        var fought = Unseen with { LastBoard = [M("BG_BEAST"), M("BG_BEAST")], LastBoardTurn = 7, LastResult = "Win", TechLevel = 3 };
        model.Update(Live(Lobby([fought])), Cards, settings, null, null);
        var badge = ((IEnumerable<dynamic>)model.BgBadges).Single();
        Assert.Equal(("BEAST", 2, 3), ((string)badge.Tribe.Key, (int)badge.TribeCount, (int)badge.Tier));
        Assert.False((bool)badge.Dead);

        model.Update(Live(Lobby([fought with { Dead = true, Health = 0, Place = 8 }])), Cards, settings, null, null);
        badge = ((IEnumerable<dynamic>)model.BgBadges).Single();
        Assert.Equal((8, true), ((int)badge.Place, (bool)badge.Dead));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Badges_show_only_in_a_battlegrounds_game_after_the_hero_pick_with_their_setting(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        dynamic model = wpf ? new WpfOverlay() : new AvaOverlay();
        var settings = new AppSettings();

        // On by default, but nothing before the view told where the leaderboard is.
        Assert.True(settings.ShowBgLeaderboardBadges);
        model.Update(Live(Lobby([Murlocs])), Cards, settings, null, null);
        Assert.False((bool)model.ShowBgBadges);
        model.ArrangeLeaderboard(1920.0, 1080.0, settings);
        Assert.True((bool)model.ShowBgBadges);

        model.Update(Live(Lobby([Murlocs], heroSelection: true)), Cards, settings, null, null);
        Assert.False((bool)model.ShowBgBadges);
        Assert.Empty((IEnumerable<dynamic>)model.BgBadges);

        settings.ShowBgLeaderboardBadges = false;
        model.Update(Live(Lobby([Murlocs])), Cards, settings, null, null);
        Assert.False((bool)model.ShowBgBadges);
        Assert.Empty((IEnumerable<dynamic>)model.BgBadges);

        settings.ShowBgLeaderboardBadges = true;
        model.Update(Live(Lobby([Murlocs])) with { InGame = false }, Cards, settings, null, null);
        Assert.False((bool)model.ShowBgBadges);
        model.Update(LiveSnapshot.Empty with { InGame = true, HearthstoneRunning = true }, Cards, settings, null, null);
        Assert.False((bool)model.ShowBgBadges);
        Assert.Empty((IEnumerable<dynamic>)model.BgBadges);

        model.Update(Live(Lobby([Murlocs])), Cards, settings, null, null);
        Assert.True((bool)model.ShowBgBadges);

        // No tier known yet: no empty shield for that place.
        model.Update(Live(Lobby([Murlocs, Unseen with { TechLevel = 0 }])), Cards, settings, null, null);
        Assert.Equal([1], ((IEnumerable<dynamic>)model.BgBadges).Select(b => (int)b.Place));
    }

    /// <summary>Each badge stands beside its tile: right of the tile by the popup's gap, over the tile's height.</summary>
    [Theory]
    [InlineData(true, 1920, 240)]
    [InlineData(false, 1920, 240)]
    [InlineData(true, 1728, 144)]
    [InlineData(false, 1728, 144)]
    [InlineData(true, 1440, 0)]
    [InlineData(false, 1440, 0)]
    public void Badges_stand_beside_the_calibrated_tiles(bool wpf, double width, double leaderboardLeft)
    {
        Loc.Instance.SetLanguage(Loc.English);
        dynamic model = wpf ? new WpfOverlay() : new AvaOverlay();
        var settings = new AppSettings();
        model.Update(Live(Lobby([Murlocs, Tie, Dead])), Cards, settings, null, null);
        model.ArrangeLeaderboard(width, 1080.0, settings);

        var slot = 0.69 / 8 * 1080;
        var badges = ((IEnumerable<dynamic>)model.BgBadges).ToList();
        Assert.All(badges, b => Assert.Equal(leaderboardLeft + ((0.10 + 0.012) * 1080), (double)b.Left, 6));
        Assert.All(badges, b => Assert.Equal(slot, (double)b.Height, 6));
        Assert.Equal(new[] { 162, 162 + (2 * slot), 162 + (7 * slot) }, badges.Select(b => (double)b.Top), new ToleranceComparer());

        // Recalibrated in the settings: the next frame moves them.
        settings.LeaderboardTop = 0.2;
        settings.LeaderboardWidth = 0.12;
        model.ArrangeLeaderboard(width, 1080.0, settings);
        badges = ((IEnumerable<dynamic>)model.BgBadges).ToList();
        Assert.Equal(216, (double)badges[0].Top, 6);
        Assert.Equal(leaderboardLeft + ((0.12 + 0.012) * 1080), (double)badges[0].Left, 6);

        // A bigger overlay scale shrinks the surface in overlay units: the badges follow it.
        model.ArrangeLeaderboard(width / 1.2, 1080.0 / 1.2, settings);
        Assert.Equal(216 / 1.2, (double)((IEnumerable<dynamic>)model.BgBadges).First().Top, 6);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Duos_badges_use_the_team_tiles(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        dynamic model = wpf ? new WpfOverlay() : new AvaOverlay();
        var settings = new AppSettings();
        model.ArrangeLeaderboard(1920.0, 1080.0, settings);
        model.Update(Live(Lobby([Murlocs, Tie, Dead], duos: true)), Cards, settings, null, null);

        var expected = new[] { 0, 2, 7 }.Select(i => WpfGeometry.Tile(i, 1080, settings, duos: true)).ToList();
        var badges = ((IEnumerable<dynamic>)model.BgBadges).ToList();
        Assert.Equal(expected.Select(t => t.Top), badges.Select(b => (double)b.Top));
        Assert.Equal(expected.Select(t => t.Height), badges.Select(b => (double)b.Height));
    }

    /// <summary>One geometry in both builds, matching the formulas the popup and the guides used before it was shared.</summary>
    [Fact]
    public void Leaderboard_geometry_is_the_same_in_both_builds()
    {
        var settings = new AppSettings();
        foreach (var (width, height) in new[] { (1920.0, 1080.0), (1728.0, 1080.0), (1440.0, 1080.0), (1200.0, 1080.0), (2560.0, 1080.0) })
        {
            var left = Math.Max(0, (width - (height * 4.0 / 3.0)) / 2);
            Assert.Equal(left, WpfGeometry.Left(width, height));
            Assert.Equal(left, AvaGeometry.Left(width, height));
            Assert.Equal(left + ((settings.LeaderboardWidth + 0.012) * height), WpfGeometry.Beside(width, height, settings));
            Assert.Equal(WpfGeometry.Beside(width, height, settings), AvaGeometry.Beside(width, height, settings));
        }

        var slot = settings.LeaderboardSlotHeight * 1080;
        var tile = slot * (1 - 0.137);
        var spacing = slot * 8 * 0.137 / 3;
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal((162 + (slot * i), slot), WpfGeometry.Tile(i, 1080, settings, duos: false), new TileComparer());
            Assert.Equal((162 + (tile * i) + (spacing * (i / 2)), tile), WpfGeometry.Tile(i, 1080, settings, duos: true), new TileComparer());
            Assert.Equal(WpfGeometry.Tile(i, 1080, settings, duos: true), AvaGeometry.Tile(i, 1080, settings, duos: true));
            Assert.Equal(WpfGeometry.Tile(i, 1080, settings, duos: false), AvaGeometry.Tile(i, 1080, settings, duos: false));
        }

        // Duos: the eight tiles still end where the solo column ends.
        var last = WpfGeometry.Tile(7, 1080, settings, duos: true);
        Assert.Equal(162 + (8 * slot), last.Top + last.Height, 6);
    }

    /// <summary>The WPF badge layer with data: on the tiles, tier and main tribe shown, the dead faded, the mouse never taken.</summary>
    [Fact]
    public void Wpf_badges_stand_on_the_tiles_and_bind_tier_and_tribe() => OnSta(() =>
    {
        Loc.Instance.SetLanguage(Loc.English);
        var model = new WpfOverlay();
        var settings = new AppSettings();
        model.ArrangeLeaderboard(1920, 1080, settings);
        model.Update(Live(Lobby([Murlocs, Unseen, Dead])), Cards, settings, null, null);

        var layer = new Hearthpulse.App.Overlay.BgBadgesLayer { DataContext = model };
        layer.Measure(new Size(1920, 1080));
        layer.Arrange(new Rect(0, 0, 1920, 1080));
        // Bindings attach at DataBind priority: let them, then lay out again.
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        layer.UpdateLayout();

        Assert.False(layer.IsHitTestVisible);
        var items = Descendants<ContentPresenter>(layer).Where(p => p.Content is Hearthpulse.App.Overlay.BgBadge).ToList();
        Assert.Equal(3, items.Count);
        var slot = 0.69 / 8 * 1080;
        Assert.All(items, i => Assert.Equal(240 + (0.112 * 1080), Canvas.GetLeft(i), 6));
        Assert.Equal(new[] { 162, 162 + (3 * slot), 162 + (7 * slot) }, items.Select(Canvas.GetTop), new ToleranceComparer());
        Assert.Equal(slot, items[0].Height, 6);

        string[] Shown(DependencyObject root) => Descendants<TextBlock>(root).Where(t => Visible(t, root)).Select(t => t.Text).ToArray();
        Assert.Equal(["4", model.BgBadges[0].Tribe!.Glyph, "4"], Shown(items[0]));
        Assert.Equal(["2"], Shown(items[1]));
        Assert.Equal(["5", model.BgBadges[2].Tribe!.Glyph, "2"], Shown(items[2]));
        var badges = Descendants<Grid>(layer).Where(g => g.Name == "Badge").ToList();
        Assert.Equal([1.0, 1.0, 0.35], badges.Select(b => Math.Round(b.Opacity, 2)));
        // Column 2 waits for the opponents' rating: sized to its content, empty for now.
        Assert.True(badges[0].ColumnDefinitions[2].Width.IsAuto);
        Assert.DoesNotContain(badges[0].Children.Cast<UIElement>(), c => Grid.GetColumn(c) == 2);
        // Centred on its tile.
        var tier = Descendants<FrameworkElement>(items[0]).Single(b => b.Name == "Tier");
        Assert.InRange(tier.TranslatePoint(new Point(0, tier.ActualHeight / 2), layer).Y, 162 + (slot / 2) - 1, 162 + (slot / 2) + 1);
    });

    /// <summary>Visible in the layer: neither the element nor any parent up to <paramref name="root"/> is collapsed.</summary>
    private static bool Visible(DependencyObject element, DependencyObject root)
    {
        for (var e = element; e is not null && e != root; e = VisualTreeHelper.GetParent(e))
        {
            if (e is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                if (app.Resources.MergedDictionaries.Count == 0)
                {
                    app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Tavern", 0, 0, 1)));
                }

                test();
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
            throw new InvalidOperationException(failure.Message, failure);
        }
    }

    private sealed class ToleranceComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-6;

        public int GetHashCode(double obj) => 0;
    }

    private sealed class TileComparer : IEqualityComparer<(double Top, double Height)>
    {
        public bool Equals((double Top, double Height) x, (double Top, double Height) y) => Math.Abs(x.Top - y.Top) < 1e-6 && Math.Abs(x.Height - y.Height) < 1e-6;

        public int GetHashCode((double Top, double Height) obj) => 0;
    }
}
