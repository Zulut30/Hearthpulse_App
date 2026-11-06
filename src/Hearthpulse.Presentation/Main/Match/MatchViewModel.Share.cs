#if HEARTHPULSE_WPF
using Hearthpulse.App.Main.Share;
#else
using Hearthpulse.UI.Main.Share;
#endif

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Match;
#else
namespace Hearthpulse.UI.Main.Match;
#endif

/// <summary>"Deck image" in the match page's header (proposal C1): the player's own deck of the game, from its code.</summary>
public sealed partial class MatchViewModel
{
    /// <summary>The game is not Battlegrounds and its deck has a code.</summary>
    public bool CanShareDeck => _page?.Game is { IsBattlegrounds: false, DeckCode.Length: > 0 };

    /// <summary>The share window of the game's deck picture; null without a code.</summary>
    public ShareViewModel? DeckShare() =>
        _page?.Game is { IsBattlegrounds: false } game ? _owner.DeckShare(game.DeckCode, game.DeckName, game.PlayerClass) : null;
}
