using System.Diagnostics;
using Hearthpulse.Core.Battlegrounds.Effects;

namespace Hearthpulse.Core.Battlegrounds;

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

    /// <summary>
    /// Card id of the entity that created this minion (its CREATOR tag), when known: e.g. a hero power that summoned it at
    /// the start of this combat. Empty when unknown.
    /// </summary>
    public string CreatorCardId { get; init; } = string.Empty;

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

    /// <summary>
    /// Numeric tags of the player-level cards in <see cref="Extras"/> at capture, by card id (a hero power's or trinket's
    /// value that grows over the game). A card missing here has no captured value: effects that need one report
    /// <see cref="UnsimulatedReason.MissingInput"/>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> ExtraTags { get; init; } = EmptyExtraTags;

    /// <summary>A numeric tag of the player-level card <paramref name="cardId"/> at capture, or null when it was not seen.</summary>
    public int? ExtraTag(string cardId, string tag) =>
        ExtraTags.TryGetValue(cardId, out var tags) && tags.TryGetValue(tag, out var value) ? value : null;

    public int TavernTier { get; init; }

    /// <summary>The hero's health plus armor when the fight starts (public on the leaderboard); 0 when unknown.</summary>
    public int Health { get; init; }

    /// <summary>The Battlegrounds turn of the fight (hero powers that unlock on a turn); 0 when unknown.</summary>
    public int Turn { get; init; }

    internal static readonly IReadOnlyDictionary<string, int> EmptyTags = new Dictionary<string, int>();

    internal static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> EmptyExtraTags =
        new Dictionary<string, IReadOnlyDictionary<string, int>>();
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

    /// <summary>The lobby's combat damage cap (<see cref="DamageRules.Cap"/>); 0 = none.</summary>
    public int DamageCap { get; init; }

    /// <summary>
    /// Estimate hero damage and lethal. Off for Duos, where the loser of one board is not the one who takes the damage.
    /// </summary>
    public bool CountDamage { get; init; } = true;
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

    /// <summary>Damage our hero takes in the fights we lose; null when we never lose (or damage is not counted).</summary>
    public DamageSpread? DamageTaken { get; init; }

    /// <summary>Damage the opponent's hero takes in the fights we win; null when we never win.</summary>
    public DamageSpread? DamageDealt { get; init; }

    /// <summary>Share of all fights that kill our hero; null when our health is unknown or damage is not counted.</summary>
    public double? LethalTaken { get; init; }

    /// <summary>Share of all fights that kill the opponent's hero; null when their health is unknown.</summary>
    public double? LethalDealt { get; init; }

    /// <summary>The combat damage cap the damage was capped at; 0 = none.</summary>
    public int DamageCap { get; init; }
}

/// <summary>Why something in a captured fight is "not simulated" (the coverage matrix uses the same words).</summary>
public enum UnsimulatedReason
{
    /// <summary>The card acts in combat and no effect is registered for it.</summary>
    MissingEffect,

    /// <summary>The effect exists but a value it needs is not in the capture (a growing value, the opponent's hidden hand).</summary>
    MissingInput,

    /// <summary>No table knows the card (newer than the data): its effect cannot be classified.</summary>
    Unknown,
}

/// <summary>One "not simulated" minion or player-level card of a side.</summary>
public sealed record UnsimulatedItem(string CardId, bool Golden, bool IsOpponent, UnsimulatedReason Reason, bool Extra)
{
    /// <summary>The reason as the matrix and simcover write it: missing_effect, missing_input, unknown.</summary>
    public string Code => SimCoverage.Code(Reason);
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
        var damage = options.CountDamage ? new DamageTally(ours, theirs, options.DamageCap) : null;
        if (ours.Minions.Count == 0 || theirs.Minions.Count == 0)
        {
            var win = ours.Minions.Count > 0 ? 1.0 : 0;
            var loss = theirs.Minions.Count > 0 ? 1.0 : 0;
            damage?.Add(win > 0 ? 1 : loss > 0 ? -1 : 0, ours.Minions.Select(m => m.TechLevel), theirs.Minions.Select(m => m.TechLevel));
            var certain = new CombatOdds(win, 1 - win - loss, loss, unsimulated);
            return damage?.With(certain) ?? certain;
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
            var outcome = fight.Run(firstAttackerIsOurs, options.FirstAttackerEntityId);
            switch (outcome)
            {
                case > 0: wins++; break;
                case < 0: losses++; break;
            }

            damage?.Add(outcome, Survivors(fight.Ours), Survivors(fight.Theirs));
        }

        if (done == 0)
        {
            return new CombatOdds(0, 1.0, 0, unsimulated) { Runs = 0 };
        }

        var winRate = Math.Clamp(wins / (double)done, 0.0, 1.0);
        var lossRate = Math.Clamp(losses / (double)done, 0.0, 1.0);
        var tieRate = Math.Clamp((done - wins - losses) / (double)done, 0.0, 1.0);

        if (wins == done)
        {
            winRate = 1.0;
            lossRate = 0.0;
            tieRate = 0.0;
        }
        else if (losses == done)
        {
            winRate = 0.0;
            lossRate = 1.0;
            tieRate = 0.0;
        }
        else if (wins + losses == 0)
        {
            winRate = 0.0;
            lossRate = 0.0;
            tieRate = 1.0;
        }
        else
        {
            tieRate = Math.Max(0.0, 1.0 - winRate - lossRate);
        }

        var odds = new CombatOdds(winRate, tieRate, lossRate, unsimulated) { Runs = done };
        return damage?.With(odds) ?? odds;
    }

    private static IEnumerable<int> Survivors(Side side)
    {
        foreach (var unit in side.Minions)
        {
            if (unit.IsAlive)
            {
                yield return unit.Spec.TechLevel;
            }
        }
    }

    /// <summary>Hero damage per finished fight, by who won (<see cref="DamageRules"/>), and the fights that were lethal.</summary>
    private sealed class DamageTally(SimSide ours, SimSide theirs, int cap)
    {
        // Damage above this is counted here (no real board gets near it: tier 7 + 7 golden tier-7 minions = 56).
        private const int MaxDamage = 80;

        private readonly int[] _taken = new int[MaxDamage + 1];
        private readonly int[] _dealt = new int[MaxDamage + 1];
        private int _fights;
        private int _lethalTaken;
        private int _lethalDealt;

        public void Add(int outcome, IEnumerable<int> ourSurvivors, IEnumerable<int> theirSurvivors)
        {
            _fights++;
            if (outcome > 0)
            {
                var dealt = DamageRules.Damage(ours.TavernTier, ourSurvivors, cap);
                _dealt[Math.Min(dealt, MaxDamage)]++;
                _lethalDealt += DamageRules.IsLethal(dealt, theirs.Health) ? 1 : 0;
            }
            else if (outcome < 0)
            {
                var taken = DamageRules.Damage(theirs.TavernTier, theirSurvivors, cap);
                _taken[Math.Min(taken, MaxDamage)]++;
                _lethalTaken += DamageRules.IsLethal(taken, ours.Health) ? 1 : 0;
            }
        }

        public CombatOdds With(CombatOdds odds) => _fights == 0
            ? odds
            : odds with
            {
                DamageTaken = DamageSpread.From(_taken),
                DamageDealt = DamageSpread.From(_dealt),
                LethalTaken = ours.Health > 0 ? _lethalTaken / (double)_fights : null,
                LethalDealt = theirs.Health > 0 ? _lethalDealt / (double)_fights : null,
                DamageCap = cap,
            };
    }

    /// <summary>
    /// Minions with unknown effects, plus player-level extras that change a fight without a registered effect (those the
    /// tables mark as acting in combat, and those no table knows: every hero power, trinket, quest reward and anomaly of
    /// the card data has a row, so an unknown one is newer than the data) or whose effect lacks a captured value.
    /// </summary>
    private static int Unsimulated(SimSide side, bool isOpponent, EffectRegistry effects) =>
        side.Minions.Count(m => m.HasUnsimulatedEffect) + side.Extras.Count(id => ExtraReason(side, id, isOpponent, effects) is not null);

    /// <summary>
    /// Why a side is "not simulated": one entry per flagged minion and per player-level card, with the reason. The count
    /// equals what <see cref="CombatOdds.Unsimulated"/> counts for this side.
    /// </summary>
    public static IReadOnlyList<UnsimulatedItem> Explain(SimSide side, bool isOpponent, EffectRegistry? effects = null)
    {
        effects ??= EffectRegistry.Default;
        var items = new List<UnsimulatedItem>();
        foreach (var m in side.Minions.Where(m => m.HasUnsimulatedEffect))
        {
            items.Add(new UnsimulatedItem(m.CardId, m.Golden, isOpponent, MinionReason(m, isOpponent, effects), Extra: false));
        }

        foreach (var id in side.Extras)
        {
            if (ExtraReason(side, id, isOpponent, effects) is { } reason)
            {
                items.Add(new UnsimulatedItem(id, Golden: false, isOpponent, reason, Extra: true));
            }
        }

        return items;
    }

    private static UnsimulatedReason? ExtraReason(SimSide side, string id, bool isOpponent, EffectRegistry effects)
    {
        var row = CombatRelevance.Find(id);
        if (row is not null && row.Relevance != Relevance.Relevant)
        {
            return null;
        }

        if (effects.Get(id, golden: false) is { } effect)
        {
            return isOpponent && effect.NeedsHand ? UnsimulatedReason.MissingInput
                : effect.HasInputs(side, id) ? null
                : UnsimulatedReason.MissingInput;
        }

        return row is null ? UnsimulatedReason.Unknown
            : SimCoverage.InputGap(id) is not null ? UnsimulatedReason.MissingInput
            : UnsimulatedReason.MissingEffect;
    }

    private static UnsimulatedReason MinionReason(SimMinion m, bool isOpponent, EffectRegistry effects)
    {
        if (effects.Get(m.CardId, m.Golden) is { NeedsHand: true } && isOpponent)
        {
            return UnsimulatedReason.MissingInput;
        }

        if (CombatRelevance.Find(m.CardId) is null && !effects.Implements(m.CardId, m.Golden, isOpponent))
        {
            return UnsimulatedReason.Unknown;
        }

        return SimCoverage.InputGap(m.CardId) is not null || m.Enchantments.Any(e => SimCoverage.InputGap(e) is not null)
            ? UnsimulatedReason.MissingInput
            : UnsimulatedReason.MissingEffect;
    }

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

                // Only when known, so boards without it keep the seeds they had.
                if (m.CreatorCardId.Length > 0)
                {
                    AddText(m.CreatorCardId);
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
