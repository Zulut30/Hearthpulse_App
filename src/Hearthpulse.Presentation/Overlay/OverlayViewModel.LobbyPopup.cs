using System.Globalization;
#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// The opponent the mouse is on in the leaderboard, shown in the "lobby-popup" panel at the top centre (where the odds
/// sit; they hide meanwhile, see <see cref="Lobby"/>). Only what the player saw: the leaderboard's place and health,
/// and the player's own fights with them - the last one, the board last seen with how long ago and its main tribe.
/// </summary>
public sealed partial class OverlayViewModel
{
    private string _lobbyKey = string.Empty;

    /// <summary>The "lobby-popup" panel shows: the mouse is on an opponent's tile.</summary>
    public bool ShowLobbyPopup => Lobby is not null;

    /// <summary>Updates the popup for leaderboard place <paramref name="slot"/> (1..8), or clears it for 0.</summary>
    public void HoverLeaderboard(int slot)
    {
        HideBgBadgesWhileHovered(slot > 0);
        OpenLobbyPopup(slot > 0 ? _bg?.Opponents.FirstOrDefault(o => o.Place == slot) : null);
    }

    /// <summary>
    /// Updates the popup for the hero the game reports under the mouse (null clears it); <paramref name="slot"/> is the
    /// tile under the mouse by the calibration, 0 for none.
    /// </summary>
    public void HoverLeaderboardHero(string? heroCardId, int slot)
    {
        BgOpponent? opponent = null;
        if (!string.IsNullOrEmpty(heroCardId) && _bg is { } bg)
        {
            opponent = bg.Opponents.FirstOrDefault(o => o.HeroCardId == heroCardId)
                       ?? bg.Opponents.FirstOrDefault(o => BaseHero(o.HeroCardId) == BaseHero(heroCardId));
        }

        HideBgBadgesWhileHovered(slot > 0 || !string.IsNullOrEmpty(heroCardId));
        OpenLobbyPopup(opponent);
    }

    /// <summary>Hero id without a skin suffix, so a skinned portrait still matches its hero.</summary>
    private static string BaseHero(string cardId)
    {
        var skin = cardId.IndexOf("_SKIN", StringComparison.OrdinalIgnoreCase);
        return skin > 0 ? cardId[..skin] : cardId;
    }

    private void OpenLobbyPopup(BgOpponent? opponent)
    {
        if (opponent is null || _bg is not { } bg)
        {
            Lobby = null;
            return;
        }

        var fights = FightsWith(opponent);
        // Built 10 times a second while the mouse rests on a tile: only a change of what it shows rebuilds it, the
        // interface language too (its texts are words).
        var key = string.Create(CultureInfo.InvariantCulture,
            $"{opponent.PlayerId}|{opponent.HeroCardId}|{opponent.Place}|{opponent.Health}|{opponent.Armor}|{opponent.Dead}|{opponent.IsNextOpponent}|{opponent.LastResult}|{opponent.LastDamage}|{opponent.LastBoardTurn}|{opponent.LastBoard.Count}|{bg.Turn}|{fights.Count}|{Loc.Instance.Language}");
        if (Lobby is not null && key == _lobbyKey)
        {
            return;
        }

        _lobbyKey = key;
        Lobby = BuildLobbyPopup(opponent, bg, fights);
    }

    private LobbyPopup BuildLobbyPopup(BgOpponent opponent, BattlegroundsView bg, List<CombatRecord> fights)
    {
        var health = opponent.Armor > 0 ? Loc.F("Overlay.LobbyPopup.HpArmor", opponent.Health, opponent.Armor) : Loc.F("Overlay.LobbyPopup.HpShort", opponent.Health);
        var summary = opponent.Place > 0 ? $"{Loc.F("Overlay.LobbyPopup.Place", opponent.Place)} · {health}" : health;
        // The board is known once a fight showed it (it may have been empty).
        var seen = opponent.LastBoardTurn > 0;
        var turnsAgo = bg.Turn - opponent.LastBoardTurn;
        // The tribe most of the board shares (as on the lobby panels), once it is at least two minions.
        var tribe = MainTribe(opponent.LastBoard, bg.Races) is { Count: >= 2 } main
            ? Loc.F("Overlay.LeaderboardBadge.TribeCount", main.Tribe.Plural, main.Count, opponent.LastBoard.Count)
            : string.Empty;
        return new LobbyPopup(
            opponent.HeroCardId,
            opponent.HeroName,
            summary,
            opponent.IsNextOpponent && !opponent.Dead,
            opponent.Dead,
            opponent.LastBoard.Select(m => new LobbyMinion(m.CardId, m.Name, m.Attack, m.Health, m.Golden, m.TechLevel)).ToList())
        {
            LastResult = opponent.LastResult switch
            {
                "Win" => Loc.F("Overlay.LobbyPopup.Won", opponent.LastDamage),
                "Loss" => Loc.F("Overlay.LobbyPopup.Lost", opponent.LastDamage),
                "Tie" => Loc.T("Overlay.LobbyPopup.Tie"),
                _ => string.Empty,
            },
            ResultKey = opponent.LastResult ?? string.Empty,
            History = fights.Count >= 2 ? fights.TakeLast(8).Select(c => new CombatDot(c.Result, string.Empty)).ToList() : [],
            LastSeen = !seen ? string.Empty : turnsAgo <= 0 ? Loc.T("Overlay.LobbyPopup.BoardThisTurn") : Loc.P("Overlay.LobbyPopup.BoardAgo", turnsAgo),
            Tribe = tribe,
            BoardNote = !seen ? Loc.T("Overlay.LobbyPopup.BoardNotSeen") : opponent.LastBoard.Count == 0 ? Loc.T("Overlay.LobbyPopup.EmptyBoard") : string.Empty,
        };
    }

    /// <summary>The fights with this opponent so far this game, oldest first.</summary>
    private List<CombatRecord> FightsWith(BgOpponent opponent) =>
        _bg?.Combats.Where(c => c.OpponentPlayerId == opponent.PlayerId).ToList() ?? [];
}
