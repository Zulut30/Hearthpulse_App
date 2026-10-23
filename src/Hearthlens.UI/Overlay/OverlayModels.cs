namespace Hearthlens.UI.Overlay;

public sealed record DeckTile(string CardId, string Name, int Cost, string Count, bool ShowCount, bool Empty, string Rarity, string Chance, bool Created);

public sealed record PlayedTile(string CardId, string Name, int Cost, string Count, string Turn, bool Created);

public sealed record HandTile(string Slot, string Turn, string Detail, bool Highlight);

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
    bool HasMeta);

/// <summary>Compact stats badge drawn above one trinket during a trinket choice.</summary>
public sealed record TrinketBadge(string CardId, string Name, string Tier, string Place, string PickRate, bool Recommended, bool HasMeta);

/// <summary>
/// One Discover option slot, above its card: rank among the options, community average placement around this
/// turn, the advisor's reasons and the Dark Gift it carries. <see cref="Rank"/> is 0 when pick suggestions are not
/// "Mark the best" (no ranking is shown then); <see cref="Tier"/> is the option's tavern tier (0 when it has none).
/// </summary>
public sealed record DiscoverBadge(int Rank, string Label, string Gift, string Placement, string PlacementNote, bool Best, bool HasData, int Tier = 0);

/// <summary>Buy hint drawn above one tavern minion; <see cref="Left"/> is in overlay units inside the shop row.</summary>
public sealed record ShopBadge(double Left, string Label, bool Best, string Placement);

public sealed record CompCardTile(string CardId, string Name, string Status, string StatusText, int Tier, bool Owned);

public sealed record CompRow(
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
