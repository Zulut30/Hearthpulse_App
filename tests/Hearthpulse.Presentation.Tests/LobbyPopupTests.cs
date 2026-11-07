using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using WpfOverlay = Hearthpulse.App.Overlay.OverlayViewModel;
using AvaOverlay = Hearthpulse.UI.Overlay.OverlayViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The opponent under the mouse on the leaderboard ("lobby-popup", top centre) and the panels next to it in the same
/// task: what the popup says in words, that the odds give it their spot, and the "session" panel's MMR line, in both
/// compiled overlays.
/// </summary>
public sealed class LobbyPopupTests
{
    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
        [
          {"id":"BG_ELEM","dbfId":21,"name":"Elemental","type":"MINION","races":["ELEMENTAL"]},
          {"id":"BG_BEAST","dbfId":22,"name":"Beast","type":"MINION","races":["BEAST"]},
          {"id":"BG_NEUTRAL","dbfId":25,"name":"Neutral","type":"MINION"},
          {"id":"HERO_ME","dbfId":26,"name":"Me","type":"HERO"}
        ]
        """)));

    private static BgMinion M(string cardId, int tier = 2, bool golden = false) => new(cardId, cardId, 3, 5, golden, tier, ["TAUNT"]);

    /// <summary>Fourth, 32 health, the board seen two turns ago: six minions, two of them elementals; the player won by 6.</summary>
    private static readonly BgOpponent Bob = new(5, "HERO_BOB", "Bob", 32, 0, 4, 4, false, 1,
        [M("BG_ELEM", 3), M("BG_ELEM"), M("BG_BEAST"), M("BG_NEUTRAL", golden: true), M("BG_NEUTRAL"), M("BG_NEUTRAL", 0)], 7, "Win", 6, IsNextOpponent: true);

    private static readonly BgOpponent Unseen = new(6, "HERO_CID", "Cid", 40, 5, 2, 6, false, 0, [], 0, null, 0, false);

    private static BattlegroundsView View(IReadOnlyList<BgOpponent> opponents, bool combat = false, bool duos = false) =>
        new(9, 4, 6, "HERO_ME", 20, 0, 2, duos, combat, false, [], ["ELEMENTAL", "BEAST"], true, opponents,
            [new CombatRecord(7, 5, "HERO_BOB", "Win", 0, 6)], [], [], null, 0, [], [])
        {
            CombatOdds = combat ? new CombatOdds(0.5, 0.2, 0.3, 0) { Runs = 1000 } : null,
        };

    private static LiveSnapshot Live(BattlegroundsView view) => LiveSnapshot.Empty with { Battlegrounds = view, InGame = true, HearthstoneRunning = true };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_popup_names_place_health_the_players_own_last_fight_and_the_board_in_words(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.Russian);
        try
        {
            dynamic model = wpf ? new WpfOverlay() : new AvaOverlay();
            model.Update(Live(View([Bob, Unseen])), Cards, new AppSettings(), null, null);
            Assert.False((bool)model.ShowLobbyPopup);

            model.HoverLeaderboard(4);
            Assert.True((bool)model.ShowLobbyPopup);
            var popup = model.Lobby;
            Assert.Equal(("HERO_BOB", "Bob"), ((string)popup.HeroCardId, (string)popup.HeroName));
            Assert.Equal("4-е место · 32 ОЗ", (string)popup.Summary);
            Assert.True((bool)popup.NextOpponent);
            Assert.False((bool)popup.Dead);
            // The player's own fight, from the player's side (design.md §1): "Your last fight: won, dealt 6".
            Assert.True((bool)popup.HasFight);
            Assert.Equal(("победа, нанесли 6", "Win"), ((string)popup.LastResult, (string)popup.ResultKey));
            Assert.Equal("Стол виден 2 хода назад", (string)popup.LastSeen);
            Assert.Equal("Элементали: 2 из 6", (string)popup.Tribe);
            Assert.Equal(string.Empty, (string)popup.BoardNote);
            // One fight with him so far: no dots yet.
            Assert.Empty((IEnumerable<dynamic>)popup.History);

            // Six portraits with attack, health, gold and the card's tier; no card text.
            var board = ((IEnumerable<dynamic>)popup.Board).ToList();
            Assert.Equal(6, board.Count);
            Assert.Equal([3, 2, 2, 2, 2, 0], board.Select(m => (int)m.Tier));
            Assert.Equal([true, true, true, true, true, false], board.Select(m => (bool)m.HasTier));
            Assert.Equal([false, false, false, true, false, false], board.Select(m => (bool)m.Golden));
            Assert.All(board, m => Assert.Equal((3, 5), ((int)m.Attack, (int)m.Health)));
            Assert.Null(((object)board[0]).GetType().GetProperty("Keywords"));

            // Armor in words, and the language follows the interface.
            model.HoverLeaderboard(6);
            Assert.Equal("6-е место · 40 ОЗ + 5 брони", (string)model.Lobby.Summary);
            Loc.Instance.SetLanguage(Loc.English);
            model.Update(Live(View([Bob, Unseen])), Cards, new AppSettings(), null, null);
            model.HoverLeaderboard(4);
            Assert.Equal(("Place 4 · 32 HP", "won, dealt 6", "Board seen 2 turns ago", "Elementals: 2 of 6"),
                ((string)model.Lobby.Summary, (string)model.Lobby.LastResult, (string)model.Lobby.LastSeen, (string)model.Lobby.Tribe));

            // The language switches while the mouse rests on the same tile: the words follow on the next hover.
            Loc.Instance.SetLanguage(Loc.Russian);
            model.HoverLeaderboard(4);
            Assert.Equal(("4-е место · 32 ОЗ", "победа, нанесли 6"), ((string)model.Lobby.Summary, (string)model.Lobby.LastResult));
            Loc.Instance.SetLanguage(Loc.English);
            model.HoverLeaderboard(4);
            Assert.Equal("Place 4 · 32 HP", (string)model.Lobby.Summary);

            // The mouse leaves: no popup.
            model.HoverLeaderboard(0);
            Assert.Null((object?)model.Lobby);
            Assert.False((bool)model.ShowLobbyPopup);
        }
        finally
        {
            Loc.Instance.SetLanguage(Loc.English);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_opponent_not_fought_yet_has_no_fight_line_and_says_the_board_was_not_seen(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        dynamic model = wpf ? new WpfOverlay() : new AvaOverlay();
        model.Update(Live(View([Bob, Unseen])), Cards, new AppSettings(), null, null);

        model.HoverLeaderboard(6);
        var popup = model.Lobby;
        Assert.False((bool)popup.HasFight);
        Assert.Equal(string.Empty, (string)popup.LastResult);
        Assert.Equal(string.Empty, (string)popup.LastSeen);
        Assert.Equal(string.Empty, (string)popup.Tribe);
        Assert.Equal("Board not seen yet", (string)popup.BoardNote);
        Assert.Empty((IEnumerable<dynamic>)popup.Board);
        Assert.False((bool)popup.NextOpponent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_opponent_who_is_out_keeps_the_last_board_and_loses_the_next_plate(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        dynamic model = wpf ? new WpfOverlay() : new AvaOverlay();
        var dead = Bob with { Dead = true, Health = 0, Place = 7 };
        // A board seen empty, this very turn.
        var empty = Unseen with { LastBoard = [], LastBoardTurn = 9, LastResult = "Tie", PlayerId = 7, HeroCardId = "HERO_DEE", Place = 3 };
        model.Update(Live(View([dead, empty])), Cards, new AppSettings(), null, null);

        model.HoverLeaderboard(7);
        Assert.True((bool)model.Lobby.Dead);
        Assert.False((bool)model.Lobby.NextOpponent);
        Assert.Equal(6, ((IEnumerable<dynamic>)model.Lobby.Board).Count());

        model.HoverLeaderboard(3);
        Assert.Equal(("tie", "Tie"), ((string)model.Lobby.LastResult, (string)model.Lobby.ResultKey));
        Assert.Equal("Board seen this turn", (string)model.Lobby.LastSeen);
        Assert.Equal("No minions on the board", (string)model.Lobby.BoardNote);
    }

    /// <summary>The odds and the popup share the top centre, as in HDT: the popup wins while the mouse is on the leaderboard.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_odds_hide_while_the_popup_shows_and_come_back_after(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        dynamic model = wpf ? new WpfOverlay() : new AvaOverlay();
        var settings = new AppSettings { ShowCombatOdds = true };
        model.Update(Live(View([Bob], combat: true)), Cards, settings, null, null);
        Assert.True((bool)model.ShowOdds);

        model.HoverLeaderboard(4);
        Assert.False((bool)model.ShowOdds);
        // A snapshot while the popup is up keeps the odds hidden.
        model.Update(Live(View([Bob], combat: true)), Cards, settings, null, null);
        Assert.False((bool)model.ShowOdds);

        model.HoverLeaderboard(0);
        Assert.True((bool)model.ShowOdds);

        // Out of combat the popup leaving brings nothing back.
        model.Update(Live(View([Bob])), Cards, settings, null, null);
        model.HoverLeaderboard(4);
        model.HoverLeaderboard(0);
        Assert.False((bool)model.ShowOdds);
    }

    /// <summary>The popup is rebuilt only when what it shows changes: the mouse resting on a tile does not rebuild it 10 times a second.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_same_tile_again_keeps_the_same_popup(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        dynamic model = wpf ? new WpfOverlay() : new AvaOverlay();
        model.Update(Live(View([Bob, Unseen])), Cards, new AppSettings(), null, null);
        model.HoverLeaderboard(4);
        var first = (object)model.Lobby;
        model.HoverLeaderboard(4);
        model.HoverLeaderboardHero("HERO_BOB", 4);
        Assert.Same(first, (object)model.Lobby);

        model.Update(Live(View([Bob with { Health = 20 }, Unseen])), Cards, new AppSettings(), null, null);
        model.HoverLeaderboard(4);
        Assert.NotSame(first, (object)model.Lobby);
        Assert.Equal("Place 4 · 20 HP", (string)model.Lobby.Summary);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_session_shows_the_mmr_from_its_start_to_the_bar_rating_and_the_change(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        dynamic model = wpf ? new WpfOverlay() : new AvaOverlay();
        var settings = new AppSettings();
        // The session's own "now" lags behind (4.png): the bar's rating is the one shown.
        var snapshot = Live(View([Bob])) with
        {
            Rating = new BattlegroundsRating(6779, 5000),
            Session = new SessionSummary(6751, 6751, 3, [1, 4, 6], 0, 0),
        };
        model.Update(snapshot, Cards, settings, null, null);
        Assert.True((bool)model.ShowSession);
        Assert.Equal($"{6751:N0} → {6779:N0}", (string)model.SessionRating);
        Assert.Equal(("+28", 28), ((string)model.SessionDelta, (int)model.SessionDeltaValue));
        Assert.Equal([true, true, false], ((IEnumerable<dynamic>)model.SessionPlaces).Select(p => (bool)p.Top));

        // At the start: the rating alone, no arrow and no change.
        model.Update(snapshot with { Rating = new BattlegroundsRating(6751, 5000) }, Cards, settings, null, null);
        Assert.Equal($"{6751:N0}", (string)model.SessionRating);
        Assert.Equal(string.Empty, (string)model.SessionDelta);

        // A loss shows with a minus sign.
        model.Update(snapshot with { Rating = new BattlegroundsRating(6700, 5000) }, Cards, settings, null, null);
        Assert.Equal(("−51", -51), ((string)model.SessionDelta, (int)model.SessionDeltaValue));

        // Duos: its own rating, no change (the session start is the solo rating).
        model.Update(Live(View([Bob], duos: true)) with { Rating = new BattlegroundsRating(6779, 5000), Session = snapshot.Session }, Cards, settings, null, null);
        Assert.Equal($"{5000:N0}", (string)model.SessionRating);
        Assert.Equal(string.Empty, (string)model.SessionDelta);

        // Nothing read yet.
        model.Update(Live(View([Bob])), Cards, settings, null, null);
        Assert.Equal("—", (string)model.SessionRating);
        Assert.Empty((IEnumerable<dynamic>)model.SessionPlaces);
    }
}
