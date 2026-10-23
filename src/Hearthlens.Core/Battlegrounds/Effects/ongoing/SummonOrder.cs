namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>Summons several different minions "left to right" where a minion is (or died).</summary>
internal static class SummonOrder
{
    /// <summary>
    /// Summons <paramref name="minions"/> in order, left to right, where <paramref name="place"/> is: in its old slot when it
    /// has died (the engine's rule for deathrattle summons), else to its right. Returns the units that were summoned (fewer
    /// when the board fills up).
    /// </summary>
    /// <remarks>
    /// <see cref="Fight.SummonAt(Unit, SimMinion, int)"/> summons copies of one minion; this is the same for different
    /// minions. A living place chains each summon to the one before. A dead place relies on the engine, which keeps the
    /// order of summons made in its slot.
    /// </remarks>
    public static List<Unit> SummonAll(Fight fight, Unit place, IReadOnlyList<SimMinion> minions)
    {
        var summoned = new List<Unit>(minions.Count);
        var anchor = place;
        foreach (var minion in minions)
        {
            if (fight.SummonAt(anchor, minion) is not { } unit)
            {
                break;
            }

            summoned.Add(unit);
            if (place.IsOnBoard)
            {
                anchor = unit;
            }
        }

        return summoned;
    }
}
