namespace Hearthpulse.Core.CardPool;

/// <summary>Which classes a pool draws from.</summary>
public enum ClassScope
{
    /// <summary>Not stated by the text: Discover offers your class and neutral cards, random effects any class.</summary>
    Default,

    /// <summary>"from your class": your class only.</summary>
    Own,

    /// <summary>Named classes ("Mage spells", "a Neutral minion").</summary>
    Named,

    /// <summary>"from another class" / "from other classes": class cards that are not yours (no neutrals).</summary>
    Other,

    /// <summary>"from any class": every class and neutral.</summary>
    Any,

    /// <summary>"from your opponent's class".</summary>
    Opponent,
}

/// <summary>How a generator picks its cards: offered as a choice, or picked at random.</summary>
public enum PoolVerb
{
    Discover,
    Random,
}

/// <summary>Game facts a pool depends on: game type, format and the two classes.</summary>
/// <param name="Format">The game's FormatType: FT_STANDARD or FT_WILD (others have no known pool).</param>
public sealed record PoolContext(string Format, string? OwnClass, string? OpponentClass)
{
    /// <summary>
    /// Game types whose generators draw from the collectible Standard/Wild cards. Arena, Tavern Brawl, Practice and
    /// adventures (GT_VS_AI), Duels and the like have pools of their own, which the catalog does not know.
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedGameTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "GT_RANKED", "GT_CASUAL", "GT_VS_FRIEND",
    };

    /// <summary>The live game's GameType; null outside a game (a pool the UI asks for).</summary>
    public string? GameType { get; init; }

    /// <summary>The player's Excavate cycle in the live game; null outside a game (the Excavate pool then shows no next tier).</summary>
    public ExcavateState? Excavate { get; init; }

    public bool IsStandard => Format == "FT_STANDARD";

    public bool IsSupported => Format is "FT_STANDARD" or "FT_WILD" && (GameType is null || SupportedGameTypes.Contains(GameType));
}

/// <summary>
/// The card filter a generator's text describes ("a Battlecry minion", "two random 3-Cost Beasts", "a spell from
/// another class that costs (5) or more"). Sets inside one field are alternatives (OR); fields combine with AND.
/// </summary>
public sealed record PoolQuery
{
    /// <summary>MINION, SPELL, WEAPON, LOCATION, HERO; empty for "card".</summary>
    public IReadOnlySet<string> Types { get; init; } = new HashSet<string>();

    public IReadOnlySet<string> Races { get; init; } = new HashSet<string>();

    public IReadOnlySet<string> Schools { get; init; } = new HashSet<string>();

    /// <summary>Death Knight runes ("a Blood Rune card", "a Blood, Frost or Unholy card"): the card's cost has one of them.</summary>
    public IReadOnlySet<string> Runes { get; init; } = new HashSet<string>();

    /// <summary>Keyword groups: each group needs one of its tags ("Combo, Battlecry, or Stealth" is one group).</summary>
    public IReadOnlyList<IReadOnlySet<string>> Mechanics { get; init; } = [];

    public string? Rarity { get; init; }

    /// <summary>Exact costs ("3-Cost", "6, 4, and 2-Cost").</summary>
    public IReadOnlySet<int> Costs { get; init; } = new HashSet<int>();

    public int? MinCost { get; init; }

    public int? MaxCost { get; init; }

    /// <summary>"odd"/"even" parity of <see cref="ParityStat"/>.</summary>
    public string? Parity { get; init; }

    /// <summary>"Cost", "Attack" or "Health".</summary>
    public string? ParityStat { get; init; }

    public int? MinAttack { get; init; }

    /// <summary>"with multiple minion types".</summary>
    public bool MultipleTypes { get; init; }

    public ClassScope Scope { get; init; }

    /// <summary>Classes for <see cref="ClassScope.Named"/>.</summary>
    public IReadOnlySet<string> Classes { get; init; } = new HashSet<string>();

    /// <summary>"from the past": cards from Wild sets only (not in Standard).</summary>
    public bool Past { get; init; }

    public bool Matches(PoolCard card, PoolContext context, PoolVerb verb, string? generatorClass)
    {
        if (Past ? card.Standard : context.IsStandard && !card.Standard)
        {
            return false;
        }

        if (Types.Count > 0 && !Types.Contains(card.Type))
        {
            return false;
        }

        if (Races.Count > 0 && !Races.Any(card.HasRace))
        {
            return false;
        }

        if (Schools.Count > 0 && (card.SpellSchool is null || !Schools.Contains(card.SpellSchool)))
        {
            return false;
        }

        if (Runes.Count > 0 && !card.Runes.Any(Runes.Contains))
        {
            return false;
        }

        foreach (var group in Mechanics)
        {
            if (!group.Any(card.HasMechanic))
            {
                return false;
            }
        }

        if (Rarity is not null && card.Rarity != Rarity)
        {
            return false;
        }

        if (Costs.Count > 0 && !Costs.Contains(card.Cost)
            || MinCost is { } min && card.Cost < min
            || MaxCost is { } max && card.Cost > max)
        {
            return false;
        }

        if (Parity is not null)
        {
            int? value = ParityStat switch { "Attack" => card.Attack, "Health" => card.Health, _ => card.Cost };
            if (value is not { } v || (v % 2 == 0) != (Parity == "even"))
            {
                return false;
            }
        }

        if (MinAttack is { } minAttack && (card.Attack ?? -1) < minAttack)
        {
            return false;
        }

        if (MultipleTypes && card.Races.Count < 2 && !card.Races.Contains("ALL"))
        {
            return false;
        }

        var own = context.OwnClass is { Length: > 0 } c && c != "NEUTRAL" ? c : generatorClass;
        return Scope switch
        {
            ClassScope.Any => true,
            ClassScope.Named => card.Classes.Any(Classes.Contains),
            ClassScope.Own => own is not null && card.HasClass(own),
            ClassScope.Opponent => context.OpponentClass is { } opponent && card.HasClass(opponent),
            ClassScope.Other => !card.IsNeutral && (own is null || !card.HasClass(own)),
            // Discover: your class and neutral cards. Random effects without a class draw from every class.
            _ => verb == PoolVerb.Random || card.IsNeutral || own is not null && card.HasClass(own),
        };
    }
}
