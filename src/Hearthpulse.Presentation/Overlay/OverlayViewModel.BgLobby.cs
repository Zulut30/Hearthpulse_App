using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// The compact Battlegrounds lobby panel ("bg-lobby"): the lobby's tribes in words, then every player's tavern tier
/// and the main tribe of the board last seen (<see cref="LobbyRows"/>). An opponent's board is known only after a
/// fight with them, as in the game, so their tribe stays empty until then.
/// </summary>
public sealed partial class OverlayViewModel
{
    private bool _showBgLobby;
    private IReadOnlyList<TribeIcon> _lobbyTribes = [];
    private bool _lobbyTribesUnconfirmed;

    public bool ShowBgLobby { get => _showBgLobby; private set => Set(ref _showBgLobby, value); }

    public IReadOnlyList<TribeIcon> LobbyTribes { get => _lobbyTribes; private set => Set(ref _lobbyTribes, value); }

    /// <summary>The game has not told the lobby's tribes yet: these are the ones seen in the tavern so far.</summary>
    public bool LobbyTribesUnconfirmed { get => _lobbyTribesUnconfirmed; private set => Set(ref _lobbyTribesUnconfirmed, value); }

    private void UpdateBgLobby(BattlegroundsView? view, AppSettings settings)
    {
        ShowBgLobby = view is not null && settings.ShowBgLobbyPanel;
        if (view is null)
        {
            return;
        }

        LobbyTribesUnconfirmed = !view.RacesConfirmed && view.Races.Count > 0;
        Replace(nameof(LobbyTribes), view.Races, r => r, () => LobbyTribes = view.Races.Select(ToTribeIcon).ToList());
    }

    /// <summary><paramref name="row"/> with the main tribe of <paramref name="board"/> (none for an empty board).</summary>
    private LobbyRow WithMainTribe(LobbyRow row, IReadOnlyList<BgMinion> board, IReadOnlyList<string> lobby) =>
        MainTribe(board, lobby) is { } main ? row with { Tribe = main.Tribe, TribeCount = main.Count, BoardCount = board.Count } : row with { BoardCount = board.Count };

    /// <summary>
    /// The tribe most minions of <paramref name="board"/> have, and how many: a minion of every tribe (amalgam) adds one
    /// to each tribe the other minions have, and a tie goes to the tribe listed first in the lobby. Null for an empty
    /// board or one with no tribe besides amalgams.
    /// </summary>
    private (TribeIcon Tribe, int Count)? MainTribe(IReadOnlyList<BgMinion> board, IReadOnlyList<string> lobby)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var amalgams = 0;
        foreach (var minion in board)
        {
            var races = _cards.Get(minion.CardId)?.Races ?? [];
            if (races.Contains("ALL"))
            {
                amalgams++;
                continue;
            }

            foreach (var race in races.Distinct())
            {
                counts[race] = counts.GetValueOrDefault(race) + 1;
            }
        }

        // Ties: lobby tribes in the lobby's order, then any other tribe by name.
        var best = counts
            .OrderByDescending(c => c.Value)
            .ThenBy(c => LobbyOrder(c.Key))
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        return best.Key is { } main ? (ToTribeIcon(main), best.Value + amalgams) : null;

        int LobbyOrder(string tribe)
        {
            for (var i = 0; i < lobby.Count; i++)
            {
                if (lobby[i] == tribe)
                {
                    return i;
                }
            }

            return int.MaxValue;
        }
    }

    /// <summary>
    /// A tribe in words: the game's name for one minion, and the plural for a count of them (the
    /// <c>Overlay.LeaderboardBadge.Tribe.*</c> keys; a tribe without one falls back to the singular).
    /// </summary>
    private TribeIcon ToTribeIcon(string race)
    {
        var name = Races.Display(race, _locale);
        return new TribeIcon(race, name, Loc.Or("Overlay.LeaderboardBadge.Tribe." + race, name));
    }
}
