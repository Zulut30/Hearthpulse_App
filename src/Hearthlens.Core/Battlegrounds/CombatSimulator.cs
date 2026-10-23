using System.Diagnostics;
using Hearthlens.Core.Battlegrounds.Effects;

namespace Hearthlens.Core.Battlegrounds;

/// <summary>One minion as it stands when the first attack of a combat starts.</summary>
/// <param name="RebornAttack">The card's printed Attack (golden-aware), which a reborn copy comes back with.</param>
/// <param name="HasUnsimulatedEffect">The card (or an enchantment on it) changes the fight in a way no registered effect models.</param>
public sealed record SimMinion(
    int Attack,
    int Health,
    bool Taunt,
    bool DivineShield,
    bool Poisonous,
    bool Venomous,
    bool Reborn,
    int AttacksPerTurn,
    bool Stealth,
    int RebornAttack,
    bool HasUnsimulatedEffect)
{
    /// <summary>Normal (non-golden) card id; effects are looked up by it with <see cref="Golden"/>. Empty when unknown.</summary>
    public string CardId { get; init; } = string.Empty;

    public bool Golden { get; init; }

    /// <summary>Power.log entity id at capture (0 for minions created by the simulator).</summary>
    public int EntityId { get; init; }

    public Tribe Tribes { get; init; }

    /// <summary>Maximum Health (the HEALTH tag); 0 means "same as <see cref="Health"/>".</summary>
    public int MaxHealth { get; init; }

    /// <summary>The card's printed Health (golden-aware); 0 when unknown.</summary>
    public int BaseHealth { get; init; }

    /// <summary>
    /// Keywords printed on the card (golden-aware). A reborn copy comes back with these (minus Reborn),
    /// not with keywords the minion gained from buffs.
    /// </summary>
    public Keyword BaseKeywords { get; init; }

    public CardMechanic Mechanics { get; init; }

    public int TechLevel { get; init; }

    /// <summary>TAG_SCRIPT_DATA_NUM_1/2 at capture: card-specific counters (e.g. the current bonus of a scaling card).</summary>
    public int ScriptData1 { get; init; }

    public int ScriptData2 { get; init; }

    /// <summary>Card ids of enchantments attached to the minion (Dark Gifts, granted deathrattles, ...).</summary>
    public IReadOnlyList<string> Enchantments { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Minions this one's start-of-combat trigger removed from the board before capture (e.g. the minion Stitched
    /// Salvager destroyed), as they were when removed.
    /// </summary>
    public IReadOnlyList<SimMinion> Remembered { get; init; } = Array.Empty<SimMinion>();

    /// <summary>Current keywords as flags.</summary>
    public Keyword Keywords =>
        (Taunt ? Keyword.Taunt : 0) | (DivineShield ? Keyword.DivineShield : 0) | (Poisonous ? Keyword.Poisonous : 0)
        | (Venomous ? Keyword.Venomous : 0) | (Reborn ? Keyword.Reborn : 0) | (Stealth ? Keyword.Stealth : 0)
        | (AttacksPerTurn >= 4 ? Keyword.MegaWindfury : AttacksPerTurn >= 2 ? Keyword.Windfury : 0);

    /// <summary>
    /// A minion with the given printed stats and keywords: a token an effect summons, or a test minion.
    /// Base stats and keywords are the given ones.
    /// </summary>
    public static SimMinion Create(string cardId, int attack, int health, Tribe tribes = Tribe.None, Keyword keywords = Keyword.None,
                                   bool golden = false, CardMechanic mechanics = CardMechanic.None) =>
        new(attack, health,
            (keywords & Keyword.Taunt) != 0,
            (keywords & Keyword.DivineShield) != 0,
            (keywords & Keyword.Poisonous) != 0,
            (keywords & Keyword.Venomous) != 0,
            (keywords & Keyword.Reborn) != 0,
            Traits.AttacksPerTurn(keywords),
            (keywords & Keyword.Stealth) != 0,
            attack,
            false)
        {
            CardId = cardId,
            Golden = golden,
            Tribes = tribes,
            MaxHealth = health,
            BaseHealth = health,
            BaseKeywords = keywords,
            Mechanics = mechanics,
        };

    /// <summary>
    /// What Reborn brings back: a fresh copy of the card with its printed Attack, 1 Health and its printed keywords
    /// (not buffs, not enchantments), golden if the minion was golden, without Reborn.
    /// </summary>
    public SimMinion RebornCopy() =>
        Create(CardId, RebornAttack, 1, Tribes, BaseKeywords & ~Keyword.Reborn, Golden, Mechanics) with
        {
            BaseHealth = BaseHealth,
            TechLevel = TechLevel,
        };
}

/// <summary>
/// One side of a combat: the minions plus what the player can see about that player. Only public information:
/// the opponent's hand and secrets are never filled in.
/// </summary>
public sealed record SimSide(IReadOnlyList<SimMinion> Minions)
{
    public string HeroCardId { get; init; } = string.Empty;

    public string HeroPowerCardId { get; init; } = string.Empty;

    /// <summary>
    /// Player-level cards (hero powers, trinkets, quest rewards, the anomaly, the Deity) whose registered effects take part
    /// in the fight. Cards without a registered effect are ignored.
    /// </summary>
    public IReadOnlyList<string> Extras { get; init; } = Array.Empty<string>();

    /// <summary>Minions in hand: the local player's only. Effects that need a hand do nothing for the opponent.</summary>
    public IReadOnlyList<SimMinion> Hand { get; init; } = Array.Empty<SimMinion>();

    /// <summary>Numeric tags of the player entity at capture, by the name Power.log prints (player-wide counters).</summary>
    public IReadOnlyDictionary<string, int> Tags { get; init; } = EmptyTags;

    public int TavernTier { get; init; }

    internal static readonly IReadOnlyDictionary<string, int> EmptyTags = new Dictionary<string, int>();
}

public sealed record SimOptions
{
    public static SimOptions Default { get; } = new();

    public int Runs { get; init; } = 4000;

    /// <summary>Stop early once this much time has passed (checked every 100 runs, after <see cref="MinRuns"/>).</summary>
    public TimeSpan? TimeBudget { get; init; }

    public int MinRuns { get; init; } = 500;

    /// <summary>Card effects to apply; null uses <see cref="EffectRegistry.Default"/>.</summary>
    public EffectRegistry? Effects { get; init; }

    /// <summary>Entity id of the minion that made the first attack, when known; attack order starts from it.</summary>
    public int FirstAttackerEntityId { get; init; }

    /// <summary>Ends the runs early (checked every 100 runs); the odds then cover the runs done so far.</summary>
    public CancellationToken Cancel { get; init; }
}

/// <summary>
/// Estimated outcome of a combat. <see cref="Unsimulated"/> counts minions and player-level cards (the Deity, anomaly...)
/// whose combat-relevant effects are ignored.
/// </summary>
public sealed record CombatOdds(double Win, double Tie, double Loss, int Unsimulated)
{
    public bool Approximate => Unsimulated > 0;

    /// <summary>Number of simulated fights behind the numbers (0 when the result needed no simulation).</summary>
    public int Runs { get; init; }
}

/// <summary>One simulated fight: +1 we won, -1 we lost, 0 tie; the surviving minions and (when traced) a log.</summary>
public sealed record FightResult(int Outcome, IReadOnlyList<SimMinion> Ours, IReadOnlyList<SimMinion> Theirs, IReadOnlyList<string> Log);

/// <summary>
/// Monte Carlo Battlegrounds combat. Keywords (taunt, divine shield, poisonous, venomous, reborn, windfury, stealth) are
/// built in; card effects plug in through <see cref="CombatEffect"/> classes found by <see cref="EffectRegistry"/>.
/// Minions and player-level cards whose effects are not implemented are reported through <see cref="CombatOdds.Unsimulated"/>.
/// Start-of-combat effects have already resolved when the boards are captured (at the first attack), so they are
/// reflected in the stats.
/// </summary>
public static class CombatSimulator
{
    /// <param name="firstAttackerIsOurs">Who attacks first if known (read from the first attack), else null.</param>
    public static CombatOdds Run(IReadOnlyList<SimMinion> ours, IReadOnlyList<SimMinion> theirs, bool? firstAttackerIsOurs, int runs = 4000) =>
        Run(new SimSide(ours), new SimSide(theirs), firstAttackerIsOurs, new SimOptions { Runs = runs });

    public static CombatOdds Run(SimSide ours, SimSide theirs, bool? firstAttackerIsOurs, SimOptions? options = null)
    {
        options ??= SimOptions.Default;
        var effects = options.Effects ?? EffectRegistry.Default;
        var unsimulated = Unsimulated(ours, isOpponent: false, effects) + Unsimulated(theirs, isOpponent: true, effects);
        if (ours.Minions.Count == 0 || theirs.Minions.Count == 0)
        {
            var win = ours.Minions.Count > 0 ? 1.0 : 0;
            var loss = theirs.Minions.Count > 0 ? 1.0 : 0;
            return new CombatOdds(win, 1 - win - loss, loss, unsimulated);
        }

        var prepared = Fight.PreparedEffects.For(ours, theirs, effects);
        // Stable seed: the same boards always show the same numbers, in every session.
        var random = new Random(Seed(ours, theirs, firstAttackerIsOurs));
        var clock = options.TimeBudget is { } ? Stopwatch.StartNew() : null;
        int done = 0, wins = 0, losses = 0;
        for (; done < options.Runs; done++)
        {
            if (done > 0 && done % 100 == 0 && options.Cancel.IsCancellationRequested)
            {
                break;
            }

            if (clock is not null && done >= options.MinRuns && done % 100 == 0 && clock.Elapsed > options.TimeBudget)
            {
                break;
            }

            var fight = new Fight(ours, theirs, effects, random, trace: false, prepared);
            switch (fight.Run(firstAttackerIsOurs, options.FirstAttackerEntityId))
            {
                case > 0: wins++; break;
                case < 0: losses++; break;
            }
        }

        return new CombatOdds(wins / (double)done, (done - wins - losses) / (double)done, losses / (double)done, unsimulated) { Runs = done };
    }

    /// <summary>
    /// Minions with unknown effects, plus player-level extras that the combat-relevance table marks as changing a fight.
    /// Unknown extras (hero powers, trinkets, cards newer than the table) cannot be classified and are not counted.
    /// </summary>
    private static int Unsimulated(SimSide side, bool isOpponent, EffectRegistry effects) =>
        side.Minions.Count(m => m.HasUnsimulatedEffect)
        + side.Extras.Count(id => CombatRelevance.Find(id) is { Relevance: Relevance.Relevant }
                                  && !effects.Implements(id, golden: false, isOpponent));

    /// <summary>Runs one fight with the given random source and returns the survivors (and a log when <paramref name="trace"/>).</summary>
    /// <remarks>Tests can pass a <see cref="Random"/> subclass overriding <see cref="Random.Next(int)"/> to script every random pick.</remarks>
    public static FightResult RunSingle(SimSide ours, SimSide theirs, bool? firstAttackerIsOurs, Random random,
                                        EffectRegistry? effects = null, bool trace = true, int firstAttackerEntityId = 0)
    {
        var fight = new Fight(ours, theirs, effects ?? EffectRegistry.Default, random, trace);
        var outcome = fight.Run(firstAttackerIsOurs, firstAttackerEntityId);
        return new FightResult(
            outcome,
            fight.Ours.Minions.Select(u => u.ToSimMinion()).ToList(),
            fight.Theirs.Minions.Select(u => u.ToSimMinion()).ToList(),
            fight.Log);
    }

    /// <summary>FNV-1a over the boards: stable across processes and machines (unlike string/record hash codes).</summary>
    internal static int Seed(SimSide ours, SimSide theirs, bool? firstAttackerIsOurs)
    {
        var hash = 14695981039346656037UL;
        void Add(long value)
        {
            for (var i = 0; i < 8; i++)
            {
                hash ^= (byte)(value >> (8 * i));
                hash *= 1099511628211UL;
            }
        }

        void AddText(string text)
        {
            Add(text.Length);
            foreach (var c in text)
            {
                Add(c);
            }
        }

        void AddSide(SimSide side)
        {
            Add(side.Minions.Count);
            foreach (var m in side.Minions)
            {
                Add(m.Attack);
                Add(m.Health);
                Add((long)m.Keywords);
                Add(m.RebornAttack);
                Add(m.HasUnsimulatedEffect ? 1 : 0);
                AddText(m.CardId);
                Add(m.Golden ? 1 : 0);
                Add((long)m.Tribes);
                Add(m.MaxHealth);
                Add((long)m.BaseKeywords);
                Add(m.ScriptData1);
                Add(m.ScriptData2);
                foreach (var enchantment in m.Enchantments)
                {
                    AddText(enchantment);
                }

                foreach (var remembered in m.Remembered)
                {
                    AddText(remembered.CardId);
                    Add(remembered.Attack);
                    Add(remembered.Health);
                }
            }

            AddText(side.HeroCardId);
            AddText(side.HeroPowerCardId);
            foreach (var extra in side.Extras)
            {
                AddText(extra);
            }

            Add(side.Hand.Count);
        }

        AddSide(ours);
        Add(-1);
        AddSide(theirs);
        Add(firstAttackerIsOurs switch { true => 1, false => 2, null => 3 });
        return (int)(hash ^ (hash >> 32));
    }
}
