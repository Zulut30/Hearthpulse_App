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

/// <summary>Marks under the opponent's hand: where each one goes and what it says on hover.</summary>
public static class OpponentHandFan
{
    /// <summary>Centre-to-centre distance of neighbouring cards while the hand is small (1080-unit overlay coordinates).</summary>
    public const double CardPitch = 92;

    /// <summary>First-to-last centre distance of a full fan; larger hands squeeze into it.</summary>
    public const double MaxSpan = 480;

    /// <summary>How much higher the outer cards of a full fan sit than the middle ones.</summary>
    public const double ArcDepth = 14;

    /// <summary>
    /// Centre of each card's mark relative to the fan centre, left to right. The game spreads a small hand at a fixed
    /// pitch and squeezes a large one into a fixed width; the fan curves, so the outer cards sit a little higher.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Positions(int count)
    {
        if (count <= 0)
        {
            return [];
        }

        var pitch = count == 1 ? 0 : Math.Min(CardPitch, MaxSpan / (count - 1));
        var half = (count - 1) / 2.0;
        var arc = ArcDepth * (pitch * (count - 1) / MaxSpan);
        var positions = new (double X, double Y)[count];
        for (var i = 0; i < count; i++)
        {
            var t = half == 0 ? 0 : (i - half) / half;
            positions[i] = ((i - half) * pitch, arc * (1 - (t * t)));
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
