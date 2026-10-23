using Hearthlens.Core.Memory;

using Hearthlens.Core.Battlegrounds;

namespace Hearthlens.Core.Tracking;

public sealed record DeckCardRow(
    string CardId,
    string Name,
    int Cost,
    int Remaining,
    int Total,
    string Rarity,
    double DrawChance,
    bool IsAdded);

public sealed record PlayedCard(string CardId, string Name, int Cost, int Count, int FirstTurn, bool IsCreated);

public sealed record HandSlot(int Position, int Turn, string? CardId, string? CardName, bool FromMulligan, bool IsCoin, string? CreatedBy);

public sealed record ConstructedView(
    int Turn,
    string? DeckName,
    bool DeckKnown,
    string PlayerHeroCardId,
    string PlayerClass,
    string OpponentHeroCardId,
    string OpponentClass,
    string? OpponentName,
    IReadOnlyList<DeckCardRow> Deck,
    int DeckCount,
    int HandCount,
    IReadOnlyList<PlayedCard> OpponentPlayed,
    IReadOnlyList<HandSlot> OpponentHand,
    int OpponentDeckCount,
    int OpponentHandCount,
    int OpponentSecrets,
    RankInfo? LocalRank,
    RankInfo? OpponentRank);

public sealed record BgMinion(
    string CardId,
    string Name,
    int Attack,
    int Health,
    bool Golden,
    int TechLevel,
    IReadOnlyList<string> Keywords)
{
    /// <summary>Card type as the client reports it: MINION, BATTLEGROUND_SPELL…</summary>
    public string CardType { get; init; } = "MINION";

    /// <summary>Live gold cost when the client reports it; 0 when unknown.</summary>
    public int Cost { get; init; }
}

public sealed record BgOpponent(
    int PlayerId,
    string HeroCardId,
    string HeroName,
    int Health,
    int Armor,
    int TechLevel,
    int Place,
    bool Dead,
    int Triples,
    IReadOnlyList<BgMinion> LastBoard,
    int LastBoardTurn,
    string? LastResult,
    int LastDamage,
    bool IsNextOpponent);

public sealed record HeroStats(int Games, double AveragePlace, double Top4Rate, double WinRate);

public sealed record HeroMeta(double AveragePosition, double AdjustedPosition, double PickRate, int DataPoints, string Tier);

public sealed record BgHeroOption(
    string CardId,
    string Name,
    int Armor,
    HeroStats? Stats,
    HeroMeta? Meta = null,
    bool Recommended = false,
    IReadOnlyList<string>? TopComps = null);

public sealed record CompCard(string CardId, string Name, string Status, int TechLevel, bool Owned);

public sealed record CompAdvice(
    string Id,
    string Name,
    string PowerLevel,
    string Difficulty,
    double AveragePlacement,
    double? HeroPlacement,
    IReadOnlyList<string> Tribes,
    IReadOnlyList<CompCard> Cards,
    string? WhenToCommit,
    string? Tip,
    int OwnedCore,
    bool IsTarget,
    bool IsPinned)
{
    /// <summary>Plain-language reason the advisor suggests this comp, e.g. "You have 2 of 4 core cards".</summary>
    public string Why { get; init; } = string.Empty;

    /// <summary>Missing core cards from tavern tiers you can reach soon, lowest tier first ("Name (T3)").</summary>
    public IReadOnlyList<string> LookFor { get; init; } = Array.Empty<string>();
}

/// <summary>Advice for one shop card (minion or tavern spell); <see cref="Slot"/> is its 0-based position in the tavern row.</summary>
public sealed record ShopPick(string CardId, string Name, int TechLevel, int Score, string Reason, bool Best, int Slot = 0)
{
    /// <summary>Community average placement of players who played this card around this turn (null without data).</summary>
    public double? Placement { get; init; }

    /// <summary>Plays behind <see cref="Placement"/>.</summary>
    public int PlacementGames { get; init; }

    /// <summary>Gold cost to buy: the live shop cost when known, else the card's base cost, else 3 for a minion.</summary>
    public int Cost { get; init; }

    /// <summary>MINION, BATTLEGROUND_SPELL…</summary>
    public string CardType { get; init; } = "MINION";

    /// <summary>True when this is a tavern spell, which goes to hand instead of the board.</summary>
    public bool IsSpell => CardType.Contains("SPELL", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A card offered by a Discover-style choice (Dark Gift button, triple reward, hero power, spell, trinket).
/// <see cref="DarkGift"/> names the Dark Gift it carries, when it has one; <see cref="CardType"/> is MINION,
/// BATTLEGROUND_SPELL, HERO_POWER…
/// </summary>
public sealed record BgDiscoverOption(string CardId, string Name, int TechLevel, string? DarkGift, string CardType = "MINION");

/// <summary>Community result for a trinket at the player's MMR bracket.</summary>
public sealed record TrinketMeta(double AveragePlacement, double PickRate, int DataPoints, string Tier);

/// <summary>One trinket offered by a Lesser/Greater Trinket choice.</summary>
public sealed record BgTrinketOption(string CardId, string Name, TrinketMeta? Meta = null, bool Recommended = false);

/// <summary>One finished combat; <see cref="Odds"/> is the simulated estimate made when it started.</summary>
public sealed record CombatRecord(int Turn, int OpponentPlayerId, string OpponentHeroCardId, string Result, int DamageTaken, int DamageDealt, CombatOdds? Odds = null);

public sealed record TierStep(int Turn, int Tier);

/// <summary>What the advisor says to do with this turn's gold.</summary>
public enum ShopAction
{
    Save,
    Buy,
    Roll,
    Level,
    Sell,
}

/// <summary>One step of a multi-action tavern plan; <see cref="Cost"/> is negative for a sell refund.</summary>
public sealed record ShopStep(
    ShopAction Action,
    string Title,
    string Detail,
    int Cost,
    int GoldLeft,
    string? CardId = null,
    string? CardName = null,
    int Slot = -1)
{
    public string ActionLabel => ShopPlan.Label(Action);
}

/// <summary>
/// Turn plan for the current tavern. The primary fields describe the main action; <see cref="Steps"/> keeps the
/// ordered sequence (sell before buy, level then buy, ...). <see cref="Cost"/> is the net gold spent by all steps,
/// negative when the plan sells more than it spends, and <see cref="GoldLeft"/> is the gold after the last step.
/// </summary>
public sealed record ShopPlan(
    ShopAction Action,
    string Title,
    string Reason,
    int Cost,
    int GoldLeft,
    string? CardId = null,
    string? CardName = null,
    int Slot = -1)
{
    public IReadOnlyList<ShopStep> Steps { get; init; } = Array.Empty<ShopStep>();

    public bool IsMultiStep => Steps.Count > 1;

    public string ActionLabel => IsMultiStep ? "PLAN" : Label(Action);

    public static string Label(ShopAction action) => action switch
    {
        ShopAction.Level => "LEVEL",
        ShopAction.Buy => "BUY",
        ShopAction.Roll => "ROLL",
        ShopAction.Sell => "SELL",
        _ => "WAIT",
    };
}

public sealed record BattlegroundsView(
    int Turn,
    int TechLevel,
    int MaxTechLevel,
    string? HeroCardId,
    int Health,
    int Armor,
    int Place,
    bool IsDuos,
    bool InCombat,
    bool IsHeroSelection,
    IReadOnlyList<BgHeroOption> HeroOptions,
    IReadOnlyList<string> Races,
    bool RacesConfirmed,
    IReadOnlyList<BgOpponent> Opponents,
    IReadOnlyList<CombatRecord> Combats,
    IReadOnlyList<TierStep> TierHistory,
    IReadOnlyList<BgMinion> Board,
    string? AnomalyCardId,
    int Triples,
    IReadOnlyList<BgMinion> Shop,
    IReadOnlyList<string> Hand)
{
    public IReadOnlyList<CompAdvice> Comps { get; init; } = Array.Empty<CompAdvice>();

    public IReadOnlyList<ShopPick> ShopAdvice { get; init; } = Array.Empty<ShopPick>();

    /// <summary>Trinkets currently offered to the player (empty when no trinket choice is open).</summary>
    public IReadOnlyList<BgTrinketOption> TrinketOptions { get; init; } = Array.Empty<BgTrinketOption>();

    /// <summary>Trinkets the player owns.</summary>
    public IReadOnlyList<string> Trinkets { get; init; } = Array.Empty<string>();

    /// <summary>The season's Dark Gift discover button is available this turn.</summary>
    public bool DarkGiftReady { get; init; }

    /// <summary>Gold cost of the Dark Gift discover button.</summary>
    public int DarkGiftCost { get; init; }

    /// <summary>Gold available this turn (0 when unknown).</summary>
    public int Gold { get; init; }

    /// <summary>
    /// Live gold cost of the tavern-upgrade button, including hero-power and trinket discounts
    /// (null when client memory is unavailable).
    /// </summary>
    public int? TavernUpgradeCost { get; init; }

    /// <summary>Live costs of all Battlegrounds buttons (freeze, reroll, upgrade, Dark Gift).</summary>
    public BattlegroundsButtonCosts? ButtonCosts { get; init; }

    /// <summary>Live reroll cost, defaulting to 1 gold when memory is unavailable.</summary>
    public int RerollCost => ButtonCosts?.Roll ?? 1;

    /// <summary>Advisor's turn plan for this tavern (null when there is nothing actionable to say).</summary>
    public ShopPlan? ShopPlan { get; init; }

    /// <summary>Advisor tip about the Dark Gift button, empty when there is nothing to say.</summary>
    public string DarkGiftAdvice { get; init; } = string.Empty;

    /// <summary>Minions of an open Discover choice, in on-screen order (empty when none is open).</summary>
    public IReadOnlyList<BgDiscoverOption> DiscoverOptions { get; init; } = Array.Empty<BgDiscoverOption>();

    /// <summary>What offers the open choice, e.g. "Hero power · Murloc King" (null when unknown or none open).</summary>
    public string? DiscoverSource { get; init; }

    /// <summary>Advisor score for each Discover option; <see cref="ShopPick.Slot"/> is its on-screen position.</summary>
    public IReadOnlyList<ShopPick> DiscoverAdvice { get; init; } = Array.Empty<ShopPick>();

    /// <summary>Best tavern spell in the current shop worth buying (null when none stands out).</summary>
    public ShopPick? BestSpell { get; init; }

    /// <summary>Tip to freeze the tavern when a key card is up but there is no gold to buy it.</summary>
    public string FreezeAdvice { get; init; } = string.Empty;

    /// <summary>Simulated odds of the combat in progress (null outside combat or before the first attack).</summary>
    public CombatOdds? CombatOdds { get; init; }

    /// <summary>Names of minions the player holds two copies of (one more makes a triple).</summary>
    public IReadOnlyList<string> Pairs { get; init; } = Array.Empty<string>();

    public string? MetaSource { get; init; }
}

public sealed record SessionSummary(int? StartRating, int? CurrentRating, int Games, IReadOnlyList<int> Placements, int Wins, int Losses)
{
    public int? Delta => StartRating is { } s && CurrentRating is { } c ? c - s : null;
}

public sealed record GameOutcome(string Mode, string Result, int Placement, int? RatingBefore, int? RatingAfter, string HeroCardId)
{
    public int? RatingDelta => RatingBefore is { } b && RatingAfter is { } a ? a - b : null;
}

public sealed record LiveSnapshot(
    string Scene,
    bool HearthstoneRunning,
    bool MemoryAttached,
    string MemoryStatus,
    string? GameType,
    bool InGame,
    ConstructedView? Constructed,
    BattlegroundsView? Battlegrounds,
    BattlegroundsRating? Rating,
    SessionSummary Session,
    GameOutcome? LastOutcome,
    IReadOnlyList<string> LogConfigMissing)
{
    public static LiveSnapshot Empty { get; } = new(
        "UNKNOWN", false, false, "Idle", null, false, null, null, null,
        new SessionSummary(null, null, 0, Array.Empty<int>(), 0, 0), null, Array.Empty<string>());
}
