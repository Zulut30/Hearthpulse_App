namespace Hearthpulse.Core.Battlegrounds;

/// <summary>
/// How much the losing hero takes after a fight: the winner's tavern tier plus the tier of every minion the winner has
/// left, capped by the lobby's combat damage cap while one is in force. Tokens without a tier count as tier 1.
/// </summary>
/// <remarks>
/// The cap is a lobby-wide rule that changes between seasons (15 until the first death in 2022, a 5/10/15 ramp until
/// Top 4 in patch 31.2, 2 then 5 on the first turns in build 254477). The game announces the current value on the game
/// entity (<c>BACON_COMBAT_DAMAGE_CAP</c>, switched by <c>BACON_COMBAT_DAMAGE_CAP_ENABLED</c> in newer builds), so it is
/// read from there instead of being hard-coded per season: see <see cref="Cap"/>.
/// </remarks>
public static class DamageRules
{
    /// <summary>Damage dealt by a winner on <paramref name="tavernTier"/> with surviving minions of the given tiers.</summary>
    /// <param name="cap">Combat damage cap; 0 or less means none.</param>
    public static int Damage(int tavernTier, IEnumerable<int> survivorTiers, int cap)
    {
        var damage = Math.Max(0, tavernTier) + survivorTiers.Sum(MinionTier);
        return cap > 0 ? Math.Min(damage, cap) : damage;
    }

    /// <summary>A minion's share of the damage: its tier, 1 for tokens (no tier).</summary>
    public static int MinionTier(int techLevel) => techLevel > 0 ? techLevel : 1;

    /// <summary>
    /// The cap in force from the game entity's tags: <paramref name="capTag"/> while the cap is on. Builds that send
    /// <c>BACON_COMBAT_DAMAGE_CAP_ENABLED</c> (<paramref name="enabledTag"/> not null) switch it off with 0 there; older
    /// builds set the cap itself to 0 once it is lifted. 0 = no cap.
    /// </summary>
    public static int Cap(int capTag, int? enabledTag) => enabledTag is 0 ? 0 : Math.Max(0, capTag);

    /// <summary>The fight kills the hero: damage reaches its health plus armor. Unknown health (0) never counts.</summary>
    public static bool IsLethal(int damage, int healthAndArmor) => healthAndArmor > 0 && damage >= healthAndArmor;
}

/// <summary>
/// Hero damage over the simulated fights one side won: the mean, an 80 % interval (<see cref="Low"/>..<see cref="High"/>,
/// the 10th and 90th percentiles), the worst case and the whole distribution.
/// </summary>
/// <param name="Counts">Fights per damage value, index = damage.</param>
public sealed record DamageSpread(double Mean, int Low, int High, int Max, IReadOnlyList<int> Counts)
{
    /// <summary>Fights behind the numbers.</summary>
    public int Runs => Counts.Sum();

    /// <summary>
    /// Builds the spread from fights per damage value; null when no fight is counted.
    /// </summary>
    public static DamageSpread? From(IReadOnlyList<int> counts)
    {
        var runs = 0;
        long total = 0;
        var max = 0;
        for (var d = 0; d < counts.Count; d++)
        {
            runs += counts[d];
            total += (long)d * counts[d];
            if (counts[d] > 0)
            {
                max = d;
            }
        }

        if (runs == 0)
        {
            return null;
        }

        return new DamageSpread(total / (double)runs, Percentile(counts, runs, 0.1), Percentile(counts, runs, 0.9), max,
            counts.Take(max + 1).ToArray());
    }

    /// <summary>Whether a damage value seen after the fight falls inside the predicted interval.</summary>
    public bool Covers(int damage) => damage >= Low && damage <= High;

    private static int Percentile(IReadOnlyList<int> counts, int runs, double share)
    {
        var target = share * runs;
        var seen = 0;
        for (var d = 0; d < counts.Count; d++)
        {
            seen += counts[d];
            if (seen >= target && seen > 0)
            {
                return d;
            }
        }

        return counts.Count - 1;
    }

    // Value equality over the distribution too, so two equal simulations compare equal.
    public bool Equals(DamageSpread? other) =>
        other is not null && Mean.Equals(other.Mean) && Low == other.Low && High == other.High && Max == other.Max
        && Counts.SequenceEqual(other.Counts);

    public override int GetHashCode() => HashCode.Combine(Mean, Low, High, Max, Counts.Count);
}
