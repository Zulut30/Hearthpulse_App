namespace Hearthpulse.Core.CardPool;

/// <summary>Why the pool panel is up.</summary>
public enum PoolTrigger
{
    /// <summary>A Discover choice from a known card is open.</summary>
    Choice,

    /// <summary>The mouse is over a generator card in hand.</summary>
    Hover,

    /// <summary>Asked for by the UI (deck or hand row hover).</summary>
    Request,
}

/// <summary>One bar of a pool histogram.</summary>
/// <param name="Height">Count relative to the tallest bar, 0..1.</param>
/// <param name="IsMedian">The bar the median falls in.</param>
public sealed record PoolBar(string Label, int Count, double Height, bool IsMedian);

/// <param name="Median">Median of the cards that have this stat.</param>
/// <param name="Count">How many pool cards have this stat (spells have no Attack).</param>
public sealed record PoolHistogram(string Title, IReadOnlyList<PoolBar> Bars, double Median, int Count)
{
    public string MedianText => Median.ToString(Median % 1 == 0 ? "0" : "0.0", System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>Share of pool cards with a mechanic.</summary>
public sealed record PoolMechanic(string Tag, string Label, int Count, double Percent)
{
    public string PercentText => (Percent / 100).ToString("P1", System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>One card of the pool, for the full list.</summary>
/// <param name="MetaGames">Games of meta decks that run the card (0 when unknown).</param>
public sealed record PoolEntry(
    string CardId,
    string Name,
    int Cost,
    int? Attack,
    int? Health,
    string Type,
    string Rarity,
    string CardClass,
    int MetaGames)
{
    /// <summary>The shown mechanics this card has (tags of <see cref="PoolKeywords.Shown"/>), for the list filters.</summary>
    public IReadOnlyList<string> Mechanics { get; init; } = [];

    /// <summary>The sections (<see cref="CardPoolView.Sections"/> labels) the card is in; empty when the pool has none.</summary>
    public IReadOnlyList<string> Sections { get; init; } = [];
}

/// <summary>One of several pools a generator draws from (a Hearthstone Wiki pool: "Blood Rune", 29 cards).</summary>
public sealed record PoolSection(string Label, int Count);

/// <summary>One Excavate tier: its number (1-4), the shown label ("Tier 2", also the section label) and its treasures.</summary>
public sealed record ExcavateTier(int Tier, string Label, string Rarity, IReadOnlyList<PoolEntry> Treasures);

/// <summary>The Excavate part of a pool view: the cycle's tiers, in order, and the one the next Excavate gives.</summary>
/// <param name="Next">1-based tier of the player's next Excavate; null outside a game.</param>
public sealed record ExcavatePool(int? Next, IReadOnlyList<ExcavateTier> Tiers)
{
    /// <summary>The tiers from the next one on, in the order the cycle gives them (all in order when the next is unknown).</summary>
    public IReadOnlyList<ExcavateTier> FromNext =>
        Next is not { } next ? Tiers : Tiers.SkipWhile(t => t.Tier != next).Concat(Tiers.TakeWhile(t => t.Tier != next)).ToList();
}

/// <summary>Everything the pool panel shows for one generator.</summary>
/// <param name="Description">The pool in words: "Battlecry minions · Mage + Neutral · Standard".</param>
/// <param name="Featured">The highlighted pool card (most played in meta decks), or null.</param>
public sealed record CardPoolView(
    string SourceCardId,
    string SourceName,
    PoolTrigger Trigger,
    string Phrase,
    string Description,
    int Count,
    PoolHistogram Cost,
    PoolHistogram? Attack,
    PoolHistogram? Health,
    IReadOnlyList<PoolMechanic> Mechanics,
    PoolEntry? Featured,
    string? FeaturedReason,
    IReadOnlyList<PoolEntry> Cards)
{
    /// <summary>Every mechanic some but not all pool cards have, highest share first: the full list's filter chips.</summary>
    public IReadOnlyList<PoolMechanic> Filters { get; init; } = [];

    /// <summary>
    /// Why the pool is not computed ("a named group of cards that the catalog does not mark"); null for a computed pool.
    /// Such a view has no cards: the panel shows "Pool not computed" with this reason instead of nothing.
    /// </summary>
    public string? Uncomputed { get; init; }

    /// <summary>Where the pool comes from: <see cref="WikiPools.Source"/>, "card text", or why the wiki list was not used.</summary>
    public string Source { get; init; } = CardPoolEngine.TextSource;

    /// <summary>The generator's separate pools (one per rune, per choice...); empty for a single pool.</summary>
    public IReadOnlyList<PoolSection> Sections { get; init; } = [];

    /// <summary>The treasures by tier for a card that Excavates (or counts Excavates); null for other pools.</summary>
    public ExcavatePool? Excavate { get; init; }
}
