using Hearthpulse.Core.Constructed;

namespace Hearthpulse.Core.Stats;

/// <summary>
/// Places an opponent in a meta archetype from the deck cards it played. Only a clear call counts: the archetype must
/// hold at least <see cref="MinHits"/> of the cards among its core cards and lead every other archetype of the class by
/// <see cref="MinLead"/>. Otherwise the opponent is just its class: a guess would mix matchups.
/// </summary>
public static class ArchetypeGuess
{
    public const int MinHits = 3;

    public const int MinLead = 2;

    /// <summary>The archetype the seen cards clearly point to, or null.</summary>
    public static MetaArchetype? FromSeenCards(ConstructedMeta? meta, string? heroClass, IEnumerable<string> seenCardIds)
    {
        if (meta is null || string.IsNullOrEmpty(heroClass))
        {
            return null;
        }

        var scored = Score(meta, heroClass, seenCardIds);
        if (scored.Count == 0 || scored[0].Hits < MinHits)
        {
            return null;
        }

        var runnerUp = scored.Count > 1 ? scored[1].Hits : 0;
        return scored[0].Hits - runnerUp >= MinLead ? scored[0].Archetype : null;
    }

    /// <summary>
    /// The archetype holding the most seen cards among its core cards (at least <see cref="MinHits"/>), even without a
    /// clear lead, and how many it holds. Only for a list the page shows as a guess; null when none reaches it.
    /// </summary>
    public static (MetaArchetype Archetype, int Hits)? Closest(ConstructedMeta? meta, string? heroClass, IEnumerable<string> seenCardIds)
    {
        if (meta is null || string.IsNullOrEmpty(heroClass))
        {
            return null;
        }

        var scored = Score(meta, heroClass, seenCardIds);
        return scored.Count > 0 && scored[0].Hits >= MinHits ? scored[0] : null;
    }

    /// <summary>The class's archetypes by how many seen cards are among their core cards, the most played first on a tie.</summary>
    private static List<(MetaArchetype Archetype, int Hits)> Score(ConstructedMeta meta, string heroClass, IEnumerable<string> seenCardIds)
    {
        var seen = new HashSet<string>(seenCardIds, StringComparer.Ordinal);
        if (seen.Count < MinHits)
        {
            return [];
        }

        return meta.ForClass(heroClass)
            .Where(a => a.CoreCards.Count > 0)
            .Select(a => (Archetype: a, Hits: a.CoreCards.Distinct(StringComparer.Ordinal).Count(seen.Contains)))
            .OrderByDescending(x => x.Hits)
            .ThenByDescending(x => x.Archetype.Games)
            .ToList();
    }


    /// <summary>True when the meta holds the game's format ("standard" for FT_STANDARD, "wild" for FT_WILD).</summary>
    public static bool FitsFormat(ConstructedMeta? meta, string? gameFormat) =>
        meta is not null && ConstructedMeta.FormatKeyOf(gameFormat) is { } key && key == meta.Format;
}
