using System.Globalization;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Tracking;

/// <summary>How a card got into the opponent's hand, as far as the log shows it.</summary>
public enum HandCardOrigin
{
    /// <summary>Drawn from their own deck: a card of the deck they started with.</summary>
    Drawn,

    /// <summary>In the opening hand, kept or redrawn in the mulligan.</summary>
    Mulligan,

    /// <summary>The Coin given to the player going second.</summary>
    Coin,

    /// <summary>Generated straight into the hand by a card (Discover, "add to your hand"...).</summary>
    Created,

    /// <summary>Drawn from the deck, but it was put there during the game (shuffled in by a card).</summary>
    Shuffled,

    /// <summary>Came back to the hand from the board or another zone (bounced, returned).</summary>
    Returned,

    /// <summary>A card that belonged to the local player (stolen or copied from you).</summary>
    Stolen,
}

/// <summary>Marks on the opponent's hand: where each one goes and what it says on hover.</summary>
public static class OpponentHandFan
{
    // Geometry of the game's own fan, measured on its screen in units of a 1080-high window (the hand scales with the
    // window height and stays put relative to its horizontal centre, whatever the width).

    /// <summary>Where the middle of the fan is, from the window's horizontal centre: the hand sits a little to the left.</summary>
    public const double FanCentre = -43;

    /// <summary>Centre-to-centre distance of neighbouring cards in a small hand (up to three cards).</summary>
    public const double CardPitch = 124;

    /// <summary>First-to-last centre distance the fan approaches as it fills up; larger hands squeeze into it.</summary>
    public const double MaxSpan = 352;

    /// <summary>How much less than <see cref="MaxSpan"/> a two-card fan spans; the gap shrinks as cards are added.</summary>
    private const double SpanShortfall = 219;

    /// <summary>How low the visible edge of the card nearest the board hangs below the top of the window.</summary>
    public const double EdgeDepth = 68;

    /// <summary>The fan's lowest point, from the window's horizontal centre (the fan tilts: its left end rises more).</summary>
    private const double LowestX = -10;

    /// <summary>How fast the card edges rise away from the lowest point (units per unit squared).</summary>
    private const double Rise = 0.0013;

    /// <summary>First-to-last centre distance of a fan of <paramref name="count"/> cards.</summary>
    public static double Span(int count) => count <= 1
        ? 0
        : Math.Min(CardPitch * (count - 1), MaxSpan - (SpanShortfall / Math.Pow(count - 1, 1.1)));

    /// <summary>
    /// Where each card's visible bottom edge is, left to right: X from the window's horizontal centre, Y from its top.
    /// The game spreads a small hand at a fixed pitch and squeezes a large one into a fixed width; the fan curves, so
    /// the outer cards sit higher, and it tilts, so the left end rises more than the right.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Positions(int count)
    {
        if (count <= 0)
        {
            return [];
        }

        var pitch = count == 1 ? 0 : Span(count) / (count - 1);
        var half = (count - 1) / 2.0;
        var positions = new (double X, double Y)[count];
        for (var i = 0; i < count; i++)
        {
            var x = FanCentre + ((i - half) * pitch);
            var dx = x - LowestX;
            positions[i] = (x, Math.Max(0, EdgeDepth - (Rise * dx * dx)));
        }

        return positions;
    }

    /// <summary>The short label on a mark: the turn the card came in, "M" for the opening hand, empty for the Coin.</summary>
    public static string Label(HandSlot slot) => slot.Origin switch
    {
        HandCardOrigin.Coin => string.Empty,
        HandCardOrigin.Mulligan => Loc.T("Core.Hand.MulliganMark"),
        _ => slot.Turn > 0 ? slot.Turn.ToString(CultureInfo.InvariantCulture) : Loc.T("Core.Hand.MulliganMark"),
    };

    /// <summary>What the card can be, for the mark's hover text.</summary>
    public static string Hint(HandSlot slot)
    {
        var turn = slot.Turn > 0 ? Loc.F("Core.Hand.OnTurn", slot.Turn.ToString(CultureInfo.InvariantCulture)) : string.Empty;
        var creator = slot.CreatedBy is null ? null : Loc.F(slot.CreatorIsYours ? "Core.Hand.YourCreator" : "Core.Hand.Creator", slot.CreatedBy);
        var origin = slot.Origin switch
        {
            HandCardOrigin.Coin => Loc.T("Core.Hand.Coin"),
            HandCardOrigin.Mulligan => Loc.T("Core.Hand.Mulligan"),
            HandCardOrigin.Created => creator is null ? Loc.F("Core.Hand.Created", turn) : Loc.F("Core.Hand.CreatedBy", creator, turn),
            HandCardOrigin.Shuffled => creator is null
                ? Loc.F("Core.Hand.PutIntoDeck", turn)
                : Loc.F("Core.Hand.ShuffledBy", creator, turn),
            HandCardOrigin.Returned => Loc.F("Core.Hand.Returned", turn),
            HandCardOrigin.Stolen => Loc.F("Core.Hand.Stolen", turn),
            _ => Loc.F("Core.Hand.Drawn", turn),
        };

        return slot.CardName is { Length: > 0 } name && slot.Origin != HandCardOrigin.Coin ? $"{name}\n{origin}" : origin;
    }
}
