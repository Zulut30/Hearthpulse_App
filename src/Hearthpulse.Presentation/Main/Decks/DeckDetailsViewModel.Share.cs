#if HEARTHPULSE_WPF
using Hearthpulse.App.Main.Share;
#else
using Hearthpulse.UI.Main.Share;
#endif

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Decks;
#else
namespace Hearthpulse.UI.Main.Decks;
#endif

/// <summary>"Deck image" on the deck page's list card (proposal C1): the main version's code as a picture.</summary>
public sealed partial class DeckDetailsViewModel
{
    /// <summary>The share window of the main version's picture; null when the deck has no code.</summary>
    public ShareViewModel? DeckShare() =>
        _family is { } family ? _owner.DeckShare(family.Main.DeckCode, family.Name, family.PlayerClass) : null;
}
