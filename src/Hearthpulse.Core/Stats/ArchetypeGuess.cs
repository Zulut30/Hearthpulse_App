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

        var seen = new HashSet<string>(seenCardIds, StringComparer.Ordinal);
        if (seen.Count < MinHits)
        {
            return null;
        }

        var scored = meta.ForClass(heroClass)
            .Where(a => a.CoreCards.Count > 0)
            .Select(a => (Archetype: a, Hits: a.CoreCards.Distinct(StringComparer.Ordinal).Count(seen.Contains)))
            .OrderByDescending(x => x.Hits)
            .ThenByDescending(x => x.Archetype.Games)
            .ToList();
        if (scored.Count == 0 || scored[0].Hits < MinHits)
        {
            return null;
        }

        var runnerUp = scored.Count > 1 ? scored[1].Hits : 0;
        return scored[0].Hits - runnerUp >= MinLead ? scored[0].Archetype : null;
    }

    /// <summary>True when the meta holds the game's format ("standard" for FT_STANDARD, "wild" for FT_WILD).</summary>
    public static bool FitsFormat(ConstructedMeta? meta, string? gameFormat) =>
        meta is not null && (gameFormat, meta.Format) is ("FT_STANDARD", "standard") or ("FT_WILD", "wild");
}
