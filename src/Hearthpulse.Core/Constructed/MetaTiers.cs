namespace Hearthpulse.Core.Constructed;

/// <summary>
/// Constructed tiers by win rate, on hearthpulse.net's scale: S from 53 %, A from 51 %, B from 49 %, C from 47 %, D
/// below. The site and the app put an archetype in the same tier.
/// </summary>
public static class MetaTiers
{
    /// <summary>Tier letters from best to worst.</summary>
    public static IReadOnlyList<string> Letters { get; } = ["S", "A", "B", "C", "D"];

    /// <summary>The lowest win rate of each tier but the last (D has no lower bound).</summary>
    private static readonly double[] Floors = [0.53, 0.51, 0.49, 0.47];

    public static string Of(double winRate)
    {
        for (var i = 0; i < Floors.Length; i++)
        {
            if (winRate >= Floors[i] - 1e-9)
            {
                return Letters[i];
            }
        }

        return Letters[^1];
    }

    /// <summary>The lower bound of a tier ("S" -> 0.53); the last tier's is that of the one above it, read as "below".</summary>
    public static double Floor(string tier)
    {
        var i = Math.Max(0, IndexOf(tier));
        return Floors[Math.Min(i, Floors.Length - 1)];
    }

    /// <summary>Whether <see cref="Floor"/> is the tier's upper bound ("below 47 %") rather than its lower one.</summary>
    public static bool IsLast(string tier) => IndexOf(tier) == Letters.Count - 1;

    private static int IndexOf(string tier)
    {
        for (var i = 0; i < Letters.Count; i++)
        {
            if (string.Equals(Letters[i], tier, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The 95 % Wilson interval of a win rate over <paramref name="games"/> games: how far the true rate can be from the
    /// measured one. Null without games.
    /// </summary>
    public static (double Low, double High)? Interval(double winRate, int games)
    {
        if (games <= 0)
        {
            return null;
        }

        const double z = 1.96;
        var p = Math.Clamp(winRate, 0, 1);
        var n = (double)games;
        var denominator = 1 + z * z / n;
        var centre = (p + z * z / (2 * n)) / denominator;
        var margin = z * Math.Sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / denominator;
        return (Math.Max(0, centre - margin), Math.Min(1, centre + margin));
    }
}
