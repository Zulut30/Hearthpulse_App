#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <param name="Changed">The card's count moved in the latest deck change (drawn, returned, shuffled in): the row flashes once.</param>
public sealed record DeckTile(string CardId, string Name, int Cost, string Count, bool ShowCount, bool Empty, string Rarity, string Chance, bool Created, bool Changed = false);

public sealed record PlayedTile(string CardId, string Name, int Cost, string Count, string Turn, bool Created);

/// <param name="Highlight">The card is known (or the Coin): drawn in full text colour.</param>
/// <param name="Unknown">Nothing is known about the card: drawn faint so the cards with a hint stand out.</param>
public sealed record HandTile(string Slot, string Turn, string Detail, bool Highlight, bool Unknown);

public sealed record TierButton(int Tier, bool IsCurrent, bool IsSelected, bool Locked);

public sealed record CombatDot(string Result, string Tooltip);

/// <summary>Compact stats badge drawn above one hero portrait during hero selection.</summary>
public sealed record HeroPickCard(
    string CardId,
    string Name,
    string Tier,
    string Place,
    string PlaceNote,
    string Delta,
    double DeltaValue,
    string PickRate,
    string Personal,
    string Comps,
    bool Recommended,
    bool HasMeta)
{
    /// <summary>Where the numbers come from and what the tier letters mean, on the tier badge instead of a caption line.</summary>
    public string TierTip { get; init; } = string.Empty;
}

/// <summary>Compact stats badge drawn above one trinket during a trinket choice.</summary>
public sealed record TrinketBadge(string CardId, string Name, string Tier, string Place, string PickRate, bool Recommended, bool HasMeta)
{
    /// <summary>Where the numbers come from and what the tier letters mean, on the tier badge instead of a caption line.</summary>
    public string TierTip { get; init; } = string.Empty;
}

/// <summary>
/// One Discover option slot, above its card: rank among the options, community average placement around this
/// turn, the advisor's reasons and the Dark Gift it carries. <see cref="Rank"/> is 0 when pick suggestions are not
/// "Mark the best" (no ranking is shown then); <see cref="Tier"/> is the option's tavern tier (0 when it has none).
/// </summary>
public sealed record DiscoverBadge(int Rank, string Label, string Gift, string Placement, string PlacementNote, bool Best, bool HasData, int Tier = 0);

/// <summary>Buy hint drawn above one tavern minion; <see cref="Left"/> is in overlay units inside the shop row.</summary>
public sealed record ShopBadge(double Left, string Label, bool Best, string Placement);

public sealed record CompCardTile(string CardId, string Name, string Status, string StatusText, int Tier, bool Owned);

public sealed partial record CompRow(
    string Id,
    string Name,
    string Power,
    string Placement,
    string HeroPlacement,
    string Difficulty,
    string Tribes,
    int OwnedCore,
    int CoreCount,
    bool IsTarget,
    bool IsPinned,
    string Why = "",
    string LookFor = "");

public sealed record BrowserGroup(string Title, IReadOnlyList<BrowserCard> Cards);

public sealed record BrowserCard(string CardId, string Name, bool InComp, string? CompStatus);

public sealed record LobbyMinion(string CardId, string Name, int Attack, int Health, bool Golden, string Keywords);

public sealed record LobbyPopup(
    string HeroCardId,
    string HeroName,
    int Health,
    int Armor,
    int Tier,
    int Place,
    int Triples,
    string LastResult,
    string LastSeen,
    bool NextOpponent,
    bool Dead,
    IReadOnlyList<LobbyMinion> Board);

public sealed record LobbyRow(
    int Place,
    string HeroCardId,
    string Name,
    int Health,
    double HealthFraction,
    int Tier,
    int Triples,
    string Last,
    string LastKey,
    bool Next,
    bool Dead,
    bool You);

public sealed record PlaceChip(int Place, bool Top);

/// <summary>One counter chip in the overlay: value, optional progress toward a goal, and the cards for its tooltip.</summary>
public sealed record CounterChip(string Id, string Title, string Value, double Progress, bool HasProgress, string? IconCardId, string Tooltip, bool Expanded, IReadOnlyList<string> Details)
{
    /// <summary>Goals of up to this many steps are drawn as segments; longer ones as a bar.</summary>
    private const int MaxSegments = 10;

    /// <summary>Tooltip lists stop here and say how many more there are.</summary>
    private const int MaxCards = 8;

    public string Description { get; init; } = string.Empty;

    /// <summary>The goal in steps, done ones first ("1/4": one lit of four); empty when the goal is drawn as a bar or there is none.</summary>
    public IReadOnlyList<bool> Segments { get; init; } = [];

    /// <summary>The parts of the goal, marked done or not (C'Thun's abilities).</summary>
    public IReadOnlyList<CounterTipCard> Parts { get; init; } = [];

    /// <summary>What was counted.</summary>
    public IReadOnlyList<CounterTipCard> Counted { get; init; } = [];

    /// <summary>"+3 more" under a cut list of counted cards, or empty.</summary>
    public string MoreCounted { get; init; } = string.Empty;

    /// <summary>The player's cards that need the count.</summary>
    public IReadOnlyList<CounterTipCard> Related { get; init; } = [];

    public string MoreRelated { get; init; } = string.Empty;

    public bool HasBar => HasProgress && Segments.Count == 0;

    /// <summary>
    /// Value equality that also compares the lists (a record compares them by reference). The overlay builds new chips
    /// on every snapshot; equal content must not count as a change, or every counter chip is rebuilt ten times a second.
    /// </summary>
    public bool Equals(CounterChip? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && Id == other.Id && Title == other.Title && Value == other.Value && Progress.Equals(other.Progress)
            && HasProgress == other.HasProgress && IconCardId == other.IconCardId && Tooltip == other.Tooltip
            && Expanded == other.Expanded && Description == other.Description
            && MoreCounted == other.MoreCounted && MoreRelated == other.MoreRelated
            && Details.SequenceEqual(other.Details) && Segments.SequenceEqual(other.Segments)
            && Parts.SequenceEqual(other.Parts) && Counted.SequenceEqual(other.Counted) && Related.SequenceEqual(other.Related));

    public override int GetHashCode() => HashCode.Combine(Id, Value, Progress, Expanded, Tooltip);

    public static CounterChip From(Hearthpulse.Core.Tracking.Counters.CounterView c, bool expanded)
    {
        var segments = c is { Goal: > 0 and <= MaxSegments, Progress: { } progress }
            ? Enumerable.Range(0, c.Goal.Value).Select(i => i < Math.Round(progress * c.Goal.Value)).ToList()
            : [];
        var hasCards = c.Parts.Count + c.Counted.Count + c.RelatedCards.Count > 0;
        return new CounterChip(
            c.Id,
            c.Title,
            c.Value,
            c.Progress ?? 0,
            c.Progress is not null,
            c.IconCardId,
            c.Details.Count == 0 ? c.Description : c.Description + Environment.NewLine + string.Join(Environment.NewLine, c.Details),
            expanded && (c.Details.Count > 0 || hasCards),
            c.Details)
        {
            Description = c.Description,
            Segments = segments,
            Parts = c.Parts.Select(p => new CounterTipCard(p.CardId, p.Name, string.Empty, true, p.Done == true)).ToList(),
            Counted = c.Counted.Take(MaxCards).Select(p => new CounterTipCard(p.CardId, p.Name, Count(p.Count), false, true)).ToList(),
            MoreCounted = More(c.Counted.Count),
            // Copies only matter when there are several.
            Related = c.RelatedCards.Take(MaxCards).Select(p => new CounterTipCard(p.CardId, p.Name, p.Count > 1 ? Count(p.Count) : string.Empty, false, true)).ToList(),
            MoreRelated = More(c.RelatedCards.Count),
        };
    }

    private static string Count(int n) => "×" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string More(int count) => count > MaxCards ? Hearthpulse.Core.Localization.Loc.F("Overlay.Counter.More", count - MaxCards) : string.Empty;
}

/// <summary>A card strip in a counter's tooltip: art, name, count, and for a part of the goal whether it is done.</summary>
public sealed record CounterTipCard(string CardId, string Name, string Count, bool IsPart, bool Done);

