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

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The compact Battlegrounds lobby panel ("bg-lobby"): the lobby's tribes, and for every player the tavern tier and the
/// main tribe of the board last seen, in both compiled overlays.
/// </summary>
public sealed class BgLobbyPanelTests
{
    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
        [
          {"id":"BG_MURLOC","dbfId":21,"name":"Murloc","type":"MINION","races":["MURLOC"]},
          {"id":"BG_BEAST","dbfId":22,"name":"Beast","type":"MINION","races":["BEAST"]},
          {"id":"BG_DRAGON","dbfId":23,"name":"Dragon","type":"MINION","races":["DRAGON"]},
          {"id":"BG_AMALGAM","dbfId":24,"name":"Amalgam","type":"MINION","races":["ALL"]},
          {"id":"BG_NEUTRAL","dbfId":25,"name":"Neutral","type":"MINION"},
          {"id":"BG_DUAL","dbfId":27,"name":"Dual","type":"MINION","races":["MURLOC","BEAST"]},
          {"id":"HERO_ME","dbfId":26,"name":"Me","type":"HERO"}
        ]
        """)));

    private static BgMinion M(string cardId) => new(cardId, cardId, 2, 2, Golden: false, TechLevel: 1, Keywords: []);

    private static BattlegroundsView Lobby(IReadOnlyList<string> races, bool confirmed, IReadOnlyList<BgOpponent> opponents, IReadOnlyList<BgMinion> board) =>
        new(7, 3, 6, "HERO_ME", 20, 0, 2, false, false, false, [], races, confirmed, opponents, [], [], board, null, 0, [], []);

    private static LiveSnapshot Live(BattlegroundsView view) => LiveSnapshot.Empty with { Battlegrounds = view, InGame = true, HearthstoneRunning = true };

    /// <summary>The panel is off by default (the lobby list shows the same players).</summary>
    private static AppSettings On() => new() { ShowBgLobbyPanel = true };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Shows_the_lobby_tribes_and_every_players_tier_and_main_tribe(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        dynamic model = wpf ? new WpfOverlay() : new AvaOverlay();
        var murlocs = new BgOpponent(2, "HERO_A", "Alice", 20, 0, 4, 1, false, 0,
            [M("BG_MURLOC"), M("BG_MURLOC"), M("BG_MURLOC"), M("BG_AMALGAM"), M("BG_BEAST"), M("BG_BEAST")], 6, "Win", 3, false);
        var tie = new BgOpponent(3, "HERO_B", "Bob", 15, 0, 3, 3, false, 0, [M("BG_BEAST"), M("BG_DRAGON"), M("BG_NEUTRAL")], 5, "Loss", 2, false);
        var unseen = new BgOpponent(4, "HERO_C", "Cid", 30, 0, 2, 4, false, 0, [], 0, null, 0, false);
        var dead = new BgOpponent(5, "HERO_D", "Dee", 0, 0, 5, 8, true, 0, [M("BG_DRAGON"), M("BG_DRAGON")], 4, "Win", 9, false);
        var view = Lobby(["DRAGON", "BEAST", "MURLOC"], confirmed: true, [murlocs, tie, unseen, dead], [M("BG_BEAST")]);
        model.Update(Live(view), Cards, On(), null, null);

        Assert.True((bool)model.ShowBgLobby);
        Assert.False((bool)model.LobbyTribesUnconfirmed);
        var tribes = ((IEnumerable<dynamic>)model.LobbyTribes).ToList();
        Assert.Equal(["DRAGON", "BEAST", "MURLOC"], tribes.Select(t => (string)t.Key));
        Assert.Equal(["Dragon", "Beast", "Murloc"], tribes.Select(t => (string)t.Name));
        Assert.Equal(3, tribes.Select(t => (string)t.Glyph).Where(g => g.Length > 0).Distinct().Count());

        var rows = ((IEnumerable<dynamic>)model.LobbyRows).ToList();
        Assert.Equal(["HERO_A", "HERO_ME", "HERO_B", "HERO_C", "HERO_D"], rows.Select(r => (string)r.HeroCardId));
        Assert.Equal([4, 3, 3, 2, 5], rows.Select(r => (int)r.Tier));
        // The amalgam adds one to each tribe on the board: three murlocs and the amalgam.
        Assert.Equal(("MURLOC", 4), ((string)rows[0].Tribe.Key, (int)rows[0].TribeCount));
        Assert.Equal(tribes[2].Glyph, rows[0].Tribe.Glyph);
        // The player's own row, from their own board.
        Assert.True((bool)rows[1].You);
        Assert.Equal(("BEAST", 1), ((string)rows[1].Tribe.Key, (int)rows[1].TribeCount));
        // A tie goes to the tribe listed first in the lobby (Dragon before Beast here), not to the alphabet.
        Assert.Equal(("DRAGON", 1), ((string)rows[2].Tribe.Key, (int)rows[2].TribeCount));
        // Never fought: nothing about the board.
        Assert.Null((object?)rows[3].Tribe);
        Assert.Equal(0, (int)rows[3].TribeCount);
        Assert.True((bool)rows[4].Dead);
        Assert.Equal(("DRAGON", 2), ((string)rows[4].Tribe.Key, (int)rows[4].TribeCount));

        // The leaderboard popup counts the same way; one minion of a tribe does not stand out there.
        model.HoverLeaderboard(1, 100.0, 200.0);
        Assert.Equal(("Murloc ×4", (string)rows[0].Tribe.Glyph), ((string)model.Lobby.Tribe, (string)model.Lobby.TribeGlyph));
        model.HoverLeaderboard(3, 100.0, 200.0);
        Assert.Equal((string.Empty, string.Empty), ((string)model.Lobby.Tribe, (string)model.Lobby.TribeGlyph));

        // A language switch renames the tribes on the next snapshot.
        Loc.Instance.SetLanguage(Loc.Russian);
        try
        {
            model.Update(Live(view), Cards, On(), null, null);
            Assert.Equal(["Дракон", "Зверь", "Мурлок"], ((IEnumerable<dynamic>)model.LobbyTribes).Select(t => (string)t.Name));
            Assert.Equal("Мурлок", (string)((IEnumerable<dynamic>)model.LobbyRows).First().Tribe.Name);
        }
        finally
        {
            Loc.Instance.SetLanguage(Loc.English);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Amalgams_add_only_to_tribes_on_the_board_and_a_two_tribe_minion_counts_for_both(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        dynamic model = wpf ? new WpfOverlay() : new AvaOverlay();
        static BgOpponent Opponent(int place, params string[] board) =>
            new(place + 10, "HERO_" + place, "P" + place, 20, 0, 3, place, false, 0, board.Select(M).ToList(), 5, "Win", 1, false);
        var view = Lobby(["DRAGON", "BEAST", "MURLOC"], confirmed: true,
        [
            Opponent(1, "BG_AMALGAM", "BG_NEUTRAL", "BG_NEUTRAL", "BG_NEUTRAL"),
            Opponent(3, "BG_AMALGAM", "BG_AMALGAM", "BG_NEUTRAL"),
            Opponent(4, "BG_DUAL", "BG_BEAST", "BG_AMALGAM"),
            Opponent(5, "BG_DUAL", "BG_MURLOC"),
        ], []);
        model.Update(Live(view), Cards, On(), null, null);

        var rows = ((IEnumerable<dynamic>)model.LobbyRows).Where(r => !(bool)r.You).ToList();
        // Amalgams and neutrals only: no main tribe (not the lobby's first tribe, which is not on the board).
        Assert.Null((object?)rows[0].Tribe);
        Assert.Null((object?)rows[1].Tribe);
        // A murloc-beast counts for both: Beast 2 + the amalgam = 3 against Murloc 1 + 1.
        Assert.Equal(("BEAST", 3), ((string)rows[2].Tribe.Key, (int)rows[2].TribeCount));
        Assert.Equal(("MURLOC", 2), ((string)rows[3].Tribe.Key, (int)rows[3].TribeCount));

        // The popup has nothing to say about two amalgams either.
        model.HoverLeaderboard(3, 100.0, 200.0);
        Assert.Equal((string.Empty, string.Empty), ((string)model.Lobby.Tribe, (string)model.Lobby.TribeGlyph));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Shows_only_in_a_battlegrounds_game_with_its_setting_and_marks_tribes_seen_only_in_the_tavern(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        dynamic model = wpf ? new WpfOverlay() : new AvaOverlay();
        var shop = Lobby(["BEAST", "MURLOC"], confirmed: false, [], []);

        // Off by default.
        var settings = new AppSettings();
        model.Update(Live(shop), Cards, settings, null, null);
        Assert.False((bool)model.ShowBgLobby);

        settings.ShowBgLobbyPanel = true;
        model.Update(Live(shop), Cards, settings, null, null);
        Assert.True((bool)model.ShowBgLobby);
        Assert.True((bool)model.LobbyTribesUnconfirmed);
        Assert.Equal(["BEAST", "MURLOC"], ((IEnumerable<dynamic>)model.LobbyTribes).Select(t => (string)t.Key));

        model.Update(Live(shop) with { InGame = false }, Cards, settings, null, null);
        Assert.False((bool)model.ShowBgLobby);
        model.Update(LiveSnapshot.Empty with { InGame = true, HearthstoneRunning = true }, Cards, settings, null, null);
        Assert.False((bool)model.ShowBgLobby);

        // Nothing read yet: no tribes and no note (the panel says it is reading the lobby).
        model.Update(Live(Lobby([], confirmed: false, [], [])), Cards, settings, null, null);
        Assert.True((bool)model.ShowBgLobby);
        Assert.Empty((IEnumerable<dynamic>)model.LobbyTribes);
        Assert.False((bool)model.LobbyTribesUnconfirmed);
    }

    /// <summary>The WPF row template with data: place, hero, tier and the main tribe; the player's name in the accent colour; the dead faded.</summary>
    [Fact]
    public void Wpf_rows_bind_tier_and_main_tribe_and_fade_the_dead() => OnSta(() =>
    {
        Loc.Instance.SetLanguage(Loc.English);
        var model = new WpfOverlay();
        var fought = new BgOpponent(2, "HERO_A", "Alice", 20, 0, 4, 1, false, 0, [M("BG_MURLOC"), M("BG_MURLOC"), M("BG_MURLOC")], 6, "Win", 3, false);
        var unseen = new BgOpponent(3, "HERO_B", "Bob", 0, 0, 2, 3, true, 0, [], 0, null, 0, false);
        model.Update(Live(Lobby(["MURLOC", "BEAST"], confirmed: false, [fought, unseen], [])), Cards, On(), null, null);

        var panel = new Hearthpulse.App.Overlay.BgLobbyPanel { DataContext = model };
        panel.Measure(new Size(400, 800));
        panel.Arrange(new Rect(0, 0, 400, 800));
        // Bindings attach at DataBind priority: let them, then lay out again.
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        panel.UpdateLayout();

        string[] Shown(DependencyObject root) => Descendants<TextBlock>(root).Where(t => Visible(t, root)).Select(t => t.Text).ToArray();
        Assert.Equal(["not confirmed", "Tribes", model.LobbyTribes[0].Glyph, "Murloc", model.LobbyTribes[1].Glyph, "Beast"], Shown(panel).Take(6));

        var rows = Descendants<Grid>(panel).Where(g => g.Name == "Row").ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(["1", "Alice", "4", model.LobbyRows[0].Tribe!.Glyph, "3"], Shown(rows[0]));
        Assert.Equal(["2", "Me", "3"], Shown(rows[1]));
        Assert.Same(Application.Current.FindResource("Accent"), Descendants<TextBlock>(rows[1]).Single(t => t.Name == "Name").Foreground);
        Assert.Equal(["3", "Bob", "2"], Shown(rows[2]));
        Assert.Equal(0.35, rows[2].Opacity, 3);
    });

    /// <summary>Visible in the panel: neither the element nor any parent up to <paramref name="root"/> is collapsed.</summary>
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
}
