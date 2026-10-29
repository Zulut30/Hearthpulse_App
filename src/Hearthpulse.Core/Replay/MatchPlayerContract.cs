using System.Globalization;

namespace Hearthpulse.Core.Replay;

/// <summary>What part of the game a replay step belongs to.</summary>
public enum MatchPhase
{
    Start,
    Mulligan,

    /// <summary>A Constructed game turn.</summary>
    Turn,

    /// <summary>A Battlegrounds recruit phase (the tavern).</summary>
    Shop,

    /// <summary>A Battlegrounds combat.</summary>
    Combat,
    End,
}

/// <summary>What happened in a replay step, in either mode.</summary>
public enum MatchStepKind
{
    Start,
    Mulligan,
    TurnStart,
    Play,
    HeroPower,
    Use,
    Attack,
    Choice,
    End,
    Buy,
    Sell,
    Upgrade,
    Refresh,
    Freeze,
    Move,
    CombatStart,
    Deaths,
    Trigger,
    CombatEnd,
}

/// <summary>
/// One step of the replay for the player bar and the timeline. <see cref="Turn"/> is the game turn (Constructed) or
/// the Battlegrounds turn. With <see cref="IsEvent"/> the left/right keys stop here; Enter/Backspace go through every
/// step.
/// </summary>
public sealed record MatchStepMark(int Frame, int Turn, MatchPhase Phase, MatchStepKind Kind, bool Local, bool IsEvent, string Caption);

/// <summary>
/// A stretch of the whole-game timeline: the mulligan, one game turn (Constructed), a recruit phase or a combat
/// (Battlegrounds).
/// </summary>
public sealed record MatchTimelineSpan(int FirstFrame, int LastFrame, int Turn, MatchPhase Phase, bool Local, string Label, string Tooltip);

/// <summary>A card on a Battlegrounds step: a minion, a tavern spell, a hero power or a trinket.</summary>
public sealed record MatchBgCardView(string CardId, string Name)
{
    public int Attack { get; init; }

    public int Health { get; init; }

    /// <summary>True for a minion (attack and health shown).</summary>
    public bool ShowStats { get; init; }

    public bool Golden { get; init; }

    public bool Damaged { get; init; }

    public bool Taunt { get; init; }

    public bool DivineShield { get; init; }

    public bool Reborn { get; init; }

    /// <summary>POISONOUS or VENOMOUS.</summary>
    public bool Venomous { get; init; }

    public bool Windfury { get; init; }

    public bool Stealth { get; init; }

    /// <summary>A frozen tavern offer.</summary>
    public bool Frozen { get; init; }

    /// <summary>The tavern tier of the card (tavern offers), 0 when not known.</summary>
    public int Tier { get; init; }

    /// <summary>The attacker of this step.</summary>
    public bool Attacking { get; init; }

    /// <summary>The defender of this step (DEFENDING).</summary>
    public bool Defending { get; init; }

    /// <summary>Died in this step.</summary>
    public bool Dying { get; init; }

    /// <summary>The card the step is about: bought, sold, played, moved, or the source of a trigger.</summary>
    public bool Acting { get; init; }

    public string Tooltip { get; init; } = string.Empty;
}

/// <summary>A hero on a Battlegrounds step: portrait, health and armor, tavern tier, hero powers and trinkets.</summary>
public sealed record MatchBgHeroView(string HeroCardId, string Name, int Health, int Armor, int Tier)
{
    public IReadOnlyList<MatchBgCardView> HeroPowers { get; init; } = [];

    /// <summary>Trinkets, the quest and its reward, in the order the game shows them.</summary>
    public IReadOnlyList<MatchBgCardView> Trinkets { get; init; } = [];

    public string HealthText => Armor > 0 ? $"{Health} + {Armor}" : Health.ToString(CultureInfo.CurrentCulture);
}

/// <summary>A row of the leaderboard at a step, as the game showed it.</summary>
public sealed record MatchBgLeaderRow(string HeroCardId, string Name, int Place, int Health, int Armor, int Tier, bool Local, bool Fighting, bool Dead);

/// <summary>One Battlegrounds step as shown: a recruit action or a combat block, the player's side at the bottom.</summary>
public sealed record MatchBgStepView(int Index, int Count, int Turn, MatchPhase Phase, MatchStepKind Kind, string Title, string Action, string Detail)
{
    public MatchBgHeroView? Hero { get; init; }

    /// <summary>The combat opponent (combat steps only).</summary>
    public MatchBgHeroView? Opponent { get; init; }

    public IReadOnlyList<MatchBgCardView> Board { get; init; } = [];

    /// <summary>The combat opponent's board as fought (combat steps only).</summary>
    public IReadOnlyList<MatchBgCardView> OpponentBoard { get; init; } = [];

    /// <summary>Bob's offer (recruit steps only).</summary>
    public IReadOnlyList<MatchBgCardView> Shop { get; init; } = [];

    public IReadOnlyList<MatchBgCardView> Hand { get; init; } = [];

    /// <summary>The step's choice (hero, Discover, trinket, quest): the options, the picked one marked.</summary>
    public IReadOnlyList<MatchChoiceView> Choices { get; init; } = [];

    public IReadOnlyList<MatchBgLeaderRow> Leaderboard { get; init; } = [];

    /// <summary>"7 / 10" in the tavern; empty in combat.</summary>
    public string Gold { get; init; } = string.Empty;

    public bool ShopFrozen { get; init; }

    /// <summary>At the end of a combat: "Win", "Loss" or "Tie"; null otherwise.</summary>
    public string? ResultKey { get; init; }

    public bool InCombat => Phase == MatchPhase.Combat;
}
