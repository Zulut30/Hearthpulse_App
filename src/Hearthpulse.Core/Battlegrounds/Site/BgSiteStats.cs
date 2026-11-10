namespace Hearthpulse.Core.Battlegrounds.Site;

/// <summary>The tier lists of the site's Battlegrounds service the tabs show.</summary>
public enum BgSiteList
{
    Heroes,
    Minions,
    Spells,
    Trinkets,
}

/// <summary>
/// One entry of a tier list (docs/bg-site-stats.md). A number the service does not publish for the list is null.
/// </summary>
/// <param name="Tier">"S", "A", "B", "C" or "D", as the service sorted it.</param>
/// <param name="NameRu">The service's Russian name; the English one when it has none.</param>
/// <param name="NameEn">Empty when the service gives no English name (heroes of the tier list): the UI reads the card database.</param>
/// <param name="TavernTier">1..7 for minions, else 0.</param>
/// <param name="PickRate">A share, 0..1.</param>
/// <param name="Top4Rate">Heroes only; a share, 0..1: places 1-4 of 8, or the top half (1-2 of 4) in a Duos list.</param>
/// <param name="FirstRate">Heroes only; a share, 0..1.</param>
/// <param name="Games">Trinkets: a lower bound, not a count (<see cref="BgSiteTierList.GamesNote"/>).</param>
/// <param name="TrinketSize">Trinkets only: "LARGE" or "SMALL".</param>
/// <param name="Cost">Trinkets only.</param>
/// <param name="Race">
/// Trinkets only: the tribe the entry is for, as the service names it ("Mech", "Quilboar"...). The service lists a
/// trinket that depends on a tribe once per tribe, under one card id, so the id alone does not tell the rows apart.
/// </param>
public sealed record BgSiteTierEntry(
    string CardId,
    int DbfId,
    string Tier,
    string NameRu,
    string NameEn,
    int TavernTier,
    double? AveragePlacement,
    double? PickRate,
    double? Top4Rate,
    double? FirstRate,
    int? Games,
    string? TrinketSize = null,
    int? Cost = null,
    string? Race = null);

/// <summary>A tier list as the service sent it.</summary>
/// <param name="Entries">The service's order: tier S first, inside a tier as sent.</param>
/// <param name="FetchedAt">When the server fetched the list, UTC; default when it does not say.</param>
/// <param name="Source">The list's <c>source</c>, verbatim.</param>
/// <param name="Mmr">The MMR slice of the list (e.g. "TOP_1_PERCENT"), when the service names it.</param>
/// <param name="TimeRange">The period of the list (e.g. "LAST_7_DAYS"), when the service names it.</param>
/// <param name="GamesNote">The service's remark on what <see cref="BgSiteTierEntry.Games"/> means (trinkets).</param>
public sealed record BgSiteTierList(
    BgSiteList Kind,
    IReadOnlyList<BgSiteTierEntry> Entries,
    IReadOnlyList<string> AvailableTiers,
    DateTime FetchedAt,
    string Source,
    string? Mmr,
    string? TimeRange,
    string? GamesNote);

/// <summary>A minion's numbers from the service's <c>minion-stats</c> (tavern spells would use the same record).</summary>
/// <param name="CombatWinRate">A share, 0..1.</param>
/// <param name="Popularity">A share, 0..1.</param>
public sealed record BgSiteCardStat(
    string CardId,
    int DbfId,
    int TavernTier,
    bool IsSpell,
    double? Impact,
    double? CombatWinRate,
    double? Popularity,
    int? GamesWith,
    int? GamesWithout,
    double? AvgPlacementWith,
    double? AvgPlacementWithout);

/// <summary>A composition of the service's comp list.</summary>
/// <param name="NameEn">Empty: the service has one title per comp.</param>
/// <param name="Tier">"S".."D", or null when the service gives another value.</param>
/// <param name="Tribes">Race keys as in <c>Races</c>; empty when the service names none.</param>
/// <param name="Difficulty">"easy", "medium" or "hard"; null when unknown.</param>
public sealed record BgSiteComp(
    string Id,
    string NameRu,
    string NameEn,
    string? Tier,
    IReadOnlyList<string> Tribes,
    IReadOnlyList<string> CoreCardIds,
    double? AveragePlacement,
    int? Games,
    string? Difficulty,
    string? Tip);

/// <summary>
/// The MMR slices the service has for one list, widest first, and the one it gives when no slice is asked for.
/// </summary>
public sealed record BgSiteSlices(IReadOnlyList<string> Mmr, string Default)
{
    /// <summary>True when <paramref name="mmr"/> is one of the slices of the list.</summary>
    public bool Has(string? mmr) => mmr is not null && Mmr.Contains(mmr, StringComparer.Ordinal);
}

/// <summary>The server run the minion numbers come from.</summary>
public sealed record BgSiteRun(string? MmrPercentile, string? TimeRange, DateTime? CompletedAt);

/// <summary>
/// Everything the Battlegrounds tabs read from the site's service. A part that could not be loaded, or whose source the
/// app refuses (docs/bg-site-stats.md, section 4), is null or empty.
/// </summary>
/// <param name="Heroes">Solo.</param>
/// <param name="HeroesDuos">Duos, the default slice (top 50%). Null when not loaded, or when the heroes answer names no allowed source to vouch for it.</param>
/// <param name="MinionStats">By card id; empty when not loaded.</param>
/// <param name="SpellStats">By card id; empty (the service's spell numbers are refused by source).</param>
/// <param name="Updated">The latest <see cref="BgSiteTierList.FetchedAt"/> of the loaded parts, UTC.</param>
/// <param name="CompsFetchedAt">When the server fetched the comps, UTC; null without comps or when it does not say.</param>
public sealed record BgSiteStats(
    BgSiteTierList? Heroes,
    BgSiteTierList? HeroesDuos,
    BgSiteTierList? Minions,
    BgSiteTierList? Spells,
    BgSiteTierList? Trinkets,
    IReadOnlyDictionary<string, BgSiteCardStat> MinionStats,
    IReadOnlyDictionary<string, BgSiteCardStat> SpellStats,
    IReadOnlyList<BgSiteComp> Comps,
    BgSiteRun? Run,
    DateTime Updated,
    DateTime? CompsFetchedAt = null);
