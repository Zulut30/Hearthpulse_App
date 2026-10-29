using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// The compact Battlegrounds lobby panel ("bg-lobby"): the lobby's tribes as icons, then every player's tavern tier
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
        MainTribe(board, lobby) is { } main ? row with { Tribe = main.Tribe, TribeCount = main.Count } : row;

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

    private TribeIcon ToTribeIcon(string race) => new(race, Races.Display(race, _locale), TribeGlyph(race));

    /// <summary>Material Design Icons glyph of a tribe (checked against the bundled font).</summary>
    private static string TribeGlyph(string race) => race switch
    {
        "BEAST" => "\U000F03E9", // paw
        "DEMON" => "\U000F0C6F", // emoticon-devil
        "DRAGON" => "\U000F1362", // google-downasaur
        "ELEMENTAL" => "\U000F0238", // fire
        "MECHANICAL" => "\U000F06A9", // robot
        "MURLOC" => "\U000F023A", // fish
        "NAGA" => "\U000F150E", // snake
        "PIRATE" => "\U000F0A08", // pirate
        "QUILBOAR" => "\U000F1006", // pig-variant
        "UNDEAD" => "\U000F068C", // skull
        _ => "\U000F070B", // rhombus: a tribe without an icon of its own
    };
}
