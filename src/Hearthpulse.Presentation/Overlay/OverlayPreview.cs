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
/// The sample game behind the overlay preview in Settings -> Overlay (D5): the real deck panel over a made-up Mage deck
/// from the card database, so the theme, size, density and panel opacity show as they will in a game. Classic cards
/// every database has; a card the database lacks is left out.
/// </summary>
public static class OverlayPreview
{
    /// <summary>The sample deck: card, copies left, copies in the deck (a drawn copy, an empty row, a legendary).</summary>
    private static readonly (string CardId, int Remaining, int Total)[] Sample =
    [
        ("CS2_027", 2, 2), // Mirror Image
        ("CS2_024", 1, 2), // Frostbolt
        ("CS2_023", 2, 2), // Arcane Intellect
        ("CS2_029", 0, 2), // Fireball, both drawn
        ("CS2_032", 1, 2), // Flamestrike
        ("EX1_559", 1, 1), // Archmage Antonidas
    ];

    /// <summary>The Mage hero of the sample.</summary>
    private const string Hero = "HERO_08";

    /// <summary>An overlay view model showing the sample deck, for the deck panel in the preview.</summary>
    public static OverlayViewModel Build(CardDb cards, AppSettings settings)
    {
        var model = new OverlayViewModel();
        model.Update(Snapshot(cards), cards, settings, null, null);
        return model;
    }

    /// <summary>A game on turn 5 with the sample deck; its draw chances follow the copies left.</summary>
    internal static LiveSnapshot Snapshot(CardDb cards)
    {
        var known = Sample.Where(s => cards.Get(s.CardId) is not null).ToList();
        var left = Math.Max(1, known.Sum(s => s.Remaining));
        var rows = known.Select(s =>
        {
            var card = cards.Get(s.CardId)!;
            return new DeckCardRow(s.CardId, card.Name, card.Cost, s.Remaining, s.Total, card.Rarity, (double)s.Remaining / left, false);
        }).ToList();
        var view = new ConstructedView(5, Loc.T("Settings.Overlay.PreviewDeck"), true, Hero, "MAGE", Hero, "MAGE", null, rows, left, 4,
            [], [], 0, 0, 0, null, null);
        return LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = view };
    }
}
