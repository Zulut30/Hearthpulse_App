using System.Text.RegularExpressions;

namespace Hearthpulse.Core.CardPool;

/// <summary>
/// Pool statistics: Cost/Attack/Health histograms with medians and the share of cards per mechanic. Mechanics come
/// from each card's mechanics and referencedTags; "Card Draw", "Deal Face Damage" and "Heal" from text rules
/// (docs/discover-pool.md).
/// </summary>
public static partial class PoolStats
{
    public const string CardDrawTag = "#CARD_DRAW";

    public const string FaceDamageTag = "#FACE_DAMAGE";

    public const string HealTag = "#HEAL";

    /// <summary>Mechanic tiles at most.</summary>
    public const int MaxMechanics = 12;

    /// <summary>Cost and Attack: 0..6 and 7+.</summary>
    public static PoolHistogram Histogram(string title, IEnumerable<int> values, int first, int last)
    {
        var list = values.Order().ToList();
        var counts = new int[last - first + 1];
        foreach (var v in list)
        {
            counts[Math.Clamp(v, first, last) - first]++;
        }

        var median = list.Count == 0 ? 0 : list.Count % 2 == 1 ? list[list.Count / 2] : (list[list.Count / 2 - 1] + list[list.Count / 2]) / 2.0;
        var medianBucket = Math.Clamp((int)Math.Floor(median), first, last) - first;
        var max = Math.Max(1, counts.Max());
        var bars = counts.Select((c, i) => new PoolBar(
            i == counts.Length - 1 ? $"{first + i}+" : (first + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
            c,
            (double)c / max,
            list.Count > 0 && i == medianBucket)).ToList();
        return new PoolHistogram(title, bars, median, list.Count);
    }

    public static (PoolHistogram Cost, PoolHistogram? Attack, PoolHistogram? Health) Histograms(IReadOnlyCollection<PoolCard> pool)
    {
        var cost = Histogram("Cost", pool.Select(c => c.Cost), 0, 7);
        var attacks = pool.Where(c => c.Type is "MINION" or "WEAPON" && c.Attack is not null).Select(c => c.Attack!.Value).ToList();
        var healths = pool.Where(c => c.Type == "MINION" && c.Health is not null).Select(c => c.Health!.Value).ToList();
        return (cost,
            attacks.Count > 0 ? Histogram("Attack", attacks, 0, 7) : null,
            healths.Count > 0 ? Histogram("Health", healths, 1, 10) : null);
    }

    /// <summary>Mechanic shares, highest first; mechanics every card has (the filter itself) or none has are left out.</summary>
    public static IReadOnlyList<PoolMechanic> Mechanics(IReadOnlyCollection<PoolCard> pool, int max = MaxMechanics)
    {
        if (pool.Count == 0)
        {
            return [];
        }

        var result = new List<PoolMechanic>();
        foreach (var (tag, label) in PoolKeywords.Shown)
        {
            var count = pool.Count(c => Has(c, tag));
            if (count > 0 && count < pool.Count)
            {
                result.Add(new PoolMechanic(tag, label, count, 100.0 * count / pool.Count));
            }
        }

        // Stable: equal shares keep the display order of PoolKeywords.Shown.
        return result.OrderByDescending(m => m.Count).Take(max).ToList();
    }

    public static bool Has(PoolCard card, string tag) => tag switch
    {
        CardDrawTag => DrawsCards(card.Text),
        FaceDamageTag => DealsFaceDamage(card.Text),
        HealTag => Heals(card.Text),
        _ => card.HasMechanic(tag) || card.ReferencedTags.Contains(tag),
    };

    /// <summary>
    /// The card draws: "Draw a card", "draw 2 minions", "Each player draws 2 cards". Not triggers on someone else's draw
    /// ("Whenever you draw a card", "your opponent draws"), "Casts When Drawn" or "when you draw this".
    /// </summary>
    public static bool DrawsCards(string text) => DrawRegex().IsMatch(text);

    /// <summary>Damage that can reach the enemy hero: "Deal 3 damage." (any target), to the enemy hero, to all enemies or characters, to random enemies, split among enemies.</summary>
    public static bool DealsFaceDamage(string text) => FaceDamageRegex().IsMatch(text);

    /// <summary>"Restore N Health", "heal", "Fully heal".</summary>
    public static bool Heals(string text) => HealRegex().IsMatch(text);

    [GeneratedRegex(@"(?<!\b(?:you|opponent|they|enemy) )\b[Dd]raws? (?:a|an|one|two|three|four|five|six|seven|\d+|that many|cards?|your|until)\b")]
    private static partial Regex DrawRegex();

    [GeneratedRegex(@"[Dd]eals? \d+ damage(?:\.|$| to (?:a character|any character|the enemy hero|your opponent|enemy hero|all enemies|all characters|both heroes|each hero|(?:a |an |\w+ )?(?:random )?enem(?:y|ies)(?! minion)))|damage (?:randomly )?split among (?:all )?enem(?:y|ies)(?! minion)|damage to (?:the enemy hero|your opponent)\b")]
    private static partial Regex FaceDamageRegex();

    [GeneratedRegex(@"\b(?:[Rr]estores?|[Hh]eals?)\b")]
    private static partial Regex HealRegex();
}
