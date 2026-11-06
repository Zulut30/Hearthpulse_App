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

public sealed partial class OverlayViewModel
{
    /// <summary>Updates the leaderboard hover popup; <paramref name="slot"/> is 1..8 or 0 for none.</summary>
    public void HoverLeaderboard(int slot, double top, double left) =>
        ShowLobbyPopup(slot > 0 ? _bg?.Opponents.FirstOrDefault(o => o.Place == slot) : null, top, left);

    /// <summary>Updates the popup for the hero the game reports under the mouse; null clears it.</summary>
    public void HoverLeaderboardHero(string? heroCardId, double top, double left)
    {
        BgOpponent? opponent = null;
        if (!string.IsNullOrEmpty(heroCardId) && _bg is { } bg)
        {
            opponent = bg.Opponents.FirstOrDefault(o => o.HeroCardId == heroCardId)
                       ?? bg.Opponents.FirstOrDefault(o => BaseHero(o.HeroCardId) == BaseHero(heroCardId));
        }

        ShowLobbyPopup(opponent, top, left);
    }

    /// <summary>Hero id without a skin suffix, so a skinned portrait still matches its hero.</summary>
    private static string BaseHero(string cardId)
    {
        var skin = cardId.IndexOf("_SKIN", StringComparison.OrdinalIgnoreCase);
        return skin > 0 ? cardId[..skin] : cardId;
    }

    private void ShowLobbyPopup(BgOpponent? opponent, double top, double left)
    {
        if (opponent is null)
        {
            Lobby = null;
            return;
        }

        LobbyTop = top;
        LobbyLeft = left;
        if (Lobby?.HeroCardId == opponent.HeroCardId && Lobby.Board.Count == opponent.LastBoard.Count && Lobby.Health == opponent.Health
            && Lobby.ResultKey == (opponent.LastResult ?? string.Empty) && Lobby.History.Count == FightsWith(opponent).Count)
        {
            return;
        }

        var turnsAgo = _bg!.Turn - opponent.LastBoardTurn;
        var fights = FightsWith(opponent);
        // The tribe most of the board shares (as on the lobby panel), once it is at least two minions.
        var tribe = MainTribe(opponent.LastBoard, _bg.Races) is { Count: >= 2 } main ? main : default;
        Lobby = new LobbyPopup(
            opponent.HeroCardId,
            opponent.HeroName,
            opponent.Health,
            opponent.Armor,
            opponent.TechLevel,
            opponent.Place,
            opponent.Triples,
            opponent.LastResult switch
            {
                null => string.Empty,
                "Win" => Loc.F("Overlay.LobbyPopup.Won", opponent.LastDamage),
                "Loss" => Loc.F("Overlay.LobbyPopup.Lost", opponent.LastDamage),
                var r => ResultText(r),
            },
            opponent.LastBoard.Count == 0 ? Loc.T("Overlay.LobbyPopup.BoardNotSeen") : turnsAgo <= 0 ? Loc.T("Overlay.LobbyPopup.BoardThisTurn") : Loc.P("Overlay.LobbyPopup.BoardAgo", turnsAgo),
            opponent.IsNextOpponent,
            opponent.Dead,
            opponent.LastBoard.Select(m => new LobbyMinion(m.CardId, m.Name, m.Attack, m.Health, m.Golden, string.Join(" · ", m.Keywords))).ToList())
        {
            ResultKey = opponent.LastResult ?? string.Empty,
            History = fights.Count >= 2 ? fights.TakeLast(8).Select(c => new CombatDot(c.Result, string.Empty)).ToList() : [],
            Tribe = tribe.Count >= 2 ? $"{tribe.Tribe.Name} ×{tribe.Count}" : string.Empty,
            TribeGlyph = tribe.Count >= 2 ? tribe.Tribe.Glyph : string.Empty,
        };
    }

    /// <summary>The fights with this opponent so far this game, oldest first.</summary>
    private List<CombatRecord> FightsWith(BgOpponent opponent) =>
        _bg?.Combats.Where(c => c.OpponentPlayerId == opponent.PlayerId).ToList() ?? [];
}
