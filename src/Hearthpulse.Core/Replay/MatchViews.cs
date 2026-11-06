using System.Globalization;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Core.Replay;

/// <summary>The match page's header: both players, the mode, the result.</summary>
public sealed record MatchHeader(
    string Title,
    string Subtitle,
    string Result,
    string ResultKey,
    MatchPlayerView Local,
    MatchPlayerView Opponent) : IImmutableView
{
    /// <summary>The game was won (Constructed) or placed in the top half (Battlegrounds).</summary>
    public bool Won => ResultKey == "Win";

    public bool Lost => ResultKey == "Loss";

    /// <summary>Header facts as label/value pairs: duration, turns, coin, deck...</summary>
    public IReadOnlyList<MatchFact> Facts { get; init; } = [];
}

public sealed record MatchPlayerView(string Name, string HeroCardId, string Hero, string Class, string Rank) : IImmutableView;

public sealed record MatchFact(string Label, string Value) : IImmutableView;

/// <summary>
/// A card at one step, ready to show. <see cref="CardBack"/> cards have no id and no name. A <see cref="Revealed"/> card
/// was a card back at this step and is shown face up because the log revealed it later in the game (match page of a
/// finished game only).
/// </summary>
public sealed record MatchCardView(string? CardId, string Name, bool CardBack) : IImmutableView
{
    public bool Revealed { get; init; }

    /// <summary>In a choice: the card picked (Discover) or kept (mulligan).</summary>
    public bool Picked { get; init; }

    /// <summary>In a choice: a card not picked, or sent back at the mulligan.</summary>
    public bool Dimmed { get; init; }

    /// <summary>The screen-reader name of an option at a choice: "Picked: Name" for the pick, else the name.</summary>
    public string ChoiceName => Picked ? Loc.F("Match.Player.HeroPicked", Name) : Name;

    public int Attack { get; init; }

    public int Health { get; init; }

    public int Cost { get; init; }

    /// <summary>Attack and health are worth showing (minions, weapons, heroes).</summary>
    public bool ShowStats { get; init; }

    public bool Damaged { get; init; }

    public bool Golden { get; init; }

    public bool Taunt { get; init; }

    public bool DivineShield { get; init; }

    /// <summary>A short note under the card: "Turn 3", "Created", "Frozen".</summary>
    public string Note { get; init; } = string.Empty;

    public string Tooltip { get; init; } = string.Empty;

    /// <summary>The card's replay id (0 for a card shown outside the table: a choice, a deck row).</summary>
    public int Ref { get; init; }

    /// <summary>The step's attacker, or the card played (plan R2).</summary>
    public bool Acting { get; init; }

    /// <summary>The step's attack target, or what the play aimed at.</summary>
    public bool Targeted { get; init; }

    /// <summary>Health gained (+) or lost (−) on this step; 0 when unchanged or new.</summary>
    public int Change { get; init; }

    /// <summary>A minion that died on this step, shown where it stood (it is gone from the next step).</summary>
    public bool Dying { get; init; }

    /// <summary>"−3" (U+2212), "+2", or empty: the change badge.</summary>
    public string ChangeText => ChangeOf(Change);

    public string ChangeTip => Change == 0 ? string.Empty : Loc.F("Match.Mark.Change", ChangeText);

    internal static string ChangeOf(int change) =>
        change < 0 ? "\u2212" + (-change).ToString(CultureInfo.CurrentCulture)
        : change > 0 ? "+" + change.ToString(CultureInfo.CurrentCulture)
        : string.Empty;
}

/// <summary>One side of the table at a step.</summary>
public sealed record MatchSideView(
    string Label,
    bool IsLocal,
    string HeroCardId,
    string Hero,
    int Health,
    int Armor,
    MatchCardView? Weapon,
    MatchCardView? HeroPower,
    IReadOnlyList<MatchCardView> Board,
    IReadOnlyList<MatchCardView> Hand,
    IReadOnlyList<MatchCardView> Secrets,
    string Mana,
    string Deck) : IImmutableView
{
    /// <summary>The health on the portrait's badge, next to a heart; the armor has its own value next to a shield.</summary>
    public string HealthText => Health.ToString(CultureInfo.CurrentCulture);

    /// <summary>The armor next to the shield ("" without armor: the shield is not shown).</summary>
    public string ArmorText => Armor > 0 ? Armor.ToString(CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>"Health 30, armor 5" on the badge's tooltip and as its screen-reader name, as on the Battlegrounds table.</summary>
    public string HealthTip => MatchBgHeroView.HealthTipOf(Health, Armor);

    /// <summary>The hero's replay id (the attack arrow's end), 0 when unknown.</summary>
    public int HeroRef { get; init; }

    /// <summary>The hero attacked this step.</summary>
    public bool HeroActing { get; init; }

    /// <summary>The hero was the attack's target, or what a play aimed at.</summary>
    public bool HeroTargeted { get; init; }

    /// <summary>Health and armor gained (+) or lost (−) on this step.</summary>
    public int HealthChange { get; init; }

    public string HealthChangeText => MatchCardView.ChangeOf(HealthChange);

    public string HealthChangeTip => HealthChange == 0 ? string.Empty : Loc.F("Match.Mark.HeroChange", HealthChangeText);

    /// <summary>Cards left in the deck.</summary>
    public int DeckCount { get; init; }

    /// <summary>Cards in hand (the opponent's: card backs).</summary>
    public int HandCount { get; init; }

    /// <summary>Secrets in play (the opponent's: face down until revealed).</summary>
    public int SecretCount { get; init; }

    /// <summary>The counters' tooltips and screen-reader names: "16 cards in deck", "9 cards in hand", "2 Secrets in play".</summary>
    public string DeckTip => Loc.P("Match.Side.DeckCount", DeckCount);

    public string HandTip => Loc.P("Match.Side.HandCount", HandCount);

    public string SecretTip => Loc.P("Match.Side.SecretCount", SecretCount);
}

/// <summary>A step of the replay as shown: the table from the chosen side and what happened.</summary>
public sealed record MatchStepView(int Index, int Count, string Turn, string Action, string Detail, bool LocalActs, MatchSideView Top, MatchSideView Bottom) : IImmutableView
{
    /// <summary>
    /// The step's acting card and its target by replay id (plan R2: the view draws an arrow between them); 0 when none
    /// is on the table. <see cref="IsAttack"/>: an attack, else a play's target (a spell, a hero power, a battlecry).
    /// </summary>
    public int ActorRef { get; init; }

    public int TargetRef { get; init; }

    public bool IsAttack { get; init; }

    /// <summary>The cards of the step's choice: the Discover options, or both mulligans at the mulligan step.</summary>
    public IReadOnlyList<MatchChoiceView> Choices { get; init; } = [];
}

/// <summary>
/// A row of cards offered at a choice ("Your mulligan", "Discover from ..."), each marked picked or not. Equal by value,
/// cards included: the player's list keeps the row of a choice shown again on the next step instead of building it anew.
/// </summary>
public sealed record MatchChoiceView(string Title, IReadOnlyList<MatchCardView> Cards) : IImmutableView
{
    /// <summary>The Battlegrounds hero choice: the views show the options as round hero portraits, not as cards.</summary>
    public bool IsHero { get; init; }

    public bool Equals(MatchChoiceView? other) =>
        other is not null && (ReferenceEquals(this, other) || (Title == other.Title && IsHero == other.IsHero && Cards.SequenceEqual(other.Cards)));

    public override int GetHashCode() => HashCode.Combine(Title, IsHero, Cards.Count);
}

/// <summary>A turn chip in the turn strip.</summary>
public sealed record MatchTurnChip(int Number, string Label, string Tooltip, bool Local, bool Mulligan, int FirstFrame, int LastFrame) : IImmutableView;

/// <summary>An action in the turn's list (clicking it goes to its step).</summary>
/// <summary>
/// A row of the game's event feed (plan R2): a turn's header (<see cref="Header"/>, its text in <see cref="Text"/>), or an
/// action of that turn, which goes to its step.
/// </summary>
public sealed record MatchFeedRow(int Frame, string Icon, string Text, bool Local, string? CardId, bool Current, bool Header) : IImmutableView
{
    /// <summary>The action is a key card's biggest swing (as <see cref="MatchActionRow.Key"/>).</summary>
    public bool Key { get; init; }

    public string? KeyReason { get; init; }

    /// <summary>A header of the mulligan (its marker is neutral).</summary>
    public bool Mulligan { get; init; }
}

public sealed record MatchActionRow(int Frame, string Icon, string Text, bool Local, string? CardId, bool Current) : IImmutableView
{
    /// <summary>The action is a key card's biggest swing (<see cref="KeyCardRow"/>): the row is marked.</summary>
    public bool Key { get; init; }

    /// <summary>Why the card was key ("Turn 6 · 2 minions killed"), the marked row's tooltip; null for other rows.</summary>
    public string? KeyReason { get; init; }
}

/// <summary>A card of a deck list: ours with draw/play marks, or one the opponent showed with its turn.</summary>
public sealed record MatchDeckRow(string CardId, string Name, int Cost, string Count, string Note, bool Used) : IImmutableView
{
    /// <summary>The card's rarity ("COMMON", "RARE", "EPIC", "LEGENDARY", "FREE"), for the row's rarity edge.</summary>
    public string Rarity { get; init; } = string.Empty;
}

/// <summary>A Battlegrounds round as shown.</summary>
public sealed record MatchRoundView(
    int Turn,
    string Title,
    string Detail,
    string Result,
    string ResultKey,
    string? OpponentHeroCardId,
    string OpponentHero,
    IReadOnlyList<MatchCardView> Board,
    IReadOnlyList<MatchCardView> OpponentBoard,
    IReadOnlyList<MatchCardView> Shop) : IImmutableView;

public sealed record MatchLobbyRow(string HeroCardId, string Hero, string Place, bool Local) : IImmutableView;
