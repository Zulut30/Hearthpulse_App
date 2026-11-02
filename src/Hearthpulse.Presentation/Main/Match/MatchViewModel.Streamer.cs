using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Match;
#else
namespace Hearthpulse.UI.Main.Match;
#endif

/// <summary>
/// Streamer mode on the match page (docs/streaming.md): only the header changes. The player's name becomes "You", the
/// opponent's name their class (Battlegrounds: "Lobby", as always) and the opponent's rank goes; the player's own rank or
/// rating stays.
/// </summary>
public sealed partial class MatchViewModel
{
    /// <summary>The page's header as streamer mode allows it.</summary>
    private MatchHeader Masked(MatchPage page) => Mask(page.Header, page.IsBattlegrounds, _owner.Settings.StreamerMode);

    /// <summary>Streamer mode was switched: the open page's header follows at once.</summary>
    internal void RefreshStreamerMode()
    {
        if (_page is { } page)
        {
            Header = Masked(page);
        }
    }

    /// <summary><paramref name="header"/> without the BattleTags and the opponent's rank while <paramref name="streamer"/> is on.</summary>
    public static MatchHeader Mask(MatchHeader header, bool battlegrounds, bool streamer)
    {
        if (!streamer)
        {
            return header;
        }

        var opponent = battlegrounds
            ? header.Opponent
            : header.Opponent with { Name = header.Opponent.Class.Length > 0 ? header.Opponent.Class : Loc.T("Match.Opponent") };
        return header with
        {
            Local = header.Local with { Name = Loc.T("Match.You") },
            Opponent = opponent with { Rank = string.Empty },
        };
    }
}
