using System.Collections.Concurrent;
using System.Reflection;

namespace Hearthlens.Core.Battlegrounds.Effects;

/// <summary>Which version of a card an effect class implements.</summary>
public enum GoldenFilter
{
    /// <summary>Normal and golden (read <see cref="Unit.Golden"/> to scale the numbers).</summary>
    Both,
    NormalOnly,
    GoldenOnly,
}

/// <summary>
/// Registers a <see cref="CombatEffect"/> class for a card (minion, token, enchantment, hero power, trinket...).
/// Use the normal card id; a golden card id is mapped to its normal id with <see cref="GoldenFilter.GoldenOnly"/>.
/// One class may carry several attributes when cards share a behaviour.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class CardEffectAttribute(string cardId) : Attribute
{
    public string CardId { get; } = cardId;

    public GoldenFilter Golden { get; set; } = GoldenFilter.Both;
}

/// <summary>Result of an attack's combat damage on its target, passed to after-attack hooks.</summary>
/// <param name="Damage">Damage that reached the target (0 when Divine Shield absorbed it).</param>
/// <param name="Killed">The target is mortally wounded or destroyed (it is still on the board until the death phase).</param>
/// <param name="Excess">Damage beyond the target's remaining Health ("excess damage"), 0 when it survived.</param>
public readonly record struct AttackOutcome(int Damage, bool Killed, int Excess);

/// <summary>Attack/Health granted by an aura.</summary>
public readonly record struct StatDelta(int Attack, int Health)
{
    public static StatDelta operator +(StatDelta a, StatDelta b) => new(a.Attack + b.Attack, a.Health + b.Health);

    public static StatDelta operator -(StatDelta a, StatDelta b) => new(a.Attack - b.Attack, a.Health - b.Health);

    public bool IsZero => Attack == 0 && Health == 0;
}

/// <summary>
/// Base class for a card's combat behaviour. Override only the hooks the card needs; the engine calls a hook only
/// on effects that override it. Instances are shared by every fight and thread, so an effect must not keep state in
/// fields: keep per-minion state on <see cref="Unit"/> (Counter, Counter2, AvengeCount) and per-player state on
/// <see cref="Side"/> (Counter(key)).
/// <para>
/// Order of one attack: <see cref="OnAttack"/> (Rally) → <see cref="OnFriendlyAttack"/> → <see cref="OnFriendlyAttacked"/> →
/// death phase (the attack is cancelled if the attacker or target died) → simultaneous combat damage and
/// <see cref="OnAttackDamage"/> (cleave) → damage hooks (<see cref="OnDamaged"/>, <see cref="AfterFriendlyDealtDamage"/>,
/// <see cref="AfterKill"/>, <see cref="OnFriendlyShieldLost"/>) → <see cref="AfterAttack"/> → <see cref="AfterFriendlyAttack"/>
/// → death phase. A death phase removes every dead minion (attacking side first, each side left to right), then per
/// dead minion runs its deathrattles (× multiplier) and its Reborn, then <see cref="AfterFriendlyDeath"/> /
/// <see cref="AfterEnemyDeath"/> for each death, refreshes auras, and repeats while minions keep dying.
/// </para>
/// </summary>
public abstract class CombatEffect
{
    private static readonly ConcurrentDictionary<Type, Hook> HookCache = new();

    protected CombatEffect()
    {
        Hooks = HookCache.GetOrAdd(GetType(), ScanHooks);
    }

    internal Hook Hooks { get; }

    /// <summary>
    /// True when the effect needs its owner's hand (Expert Aviator, Diremuck Forager...). The opponent's hand is hidden,
    /// so such a minion stays "not simulated" on the opponent's side.
    /// </summary>
    public virtual bool NeedsHand => false;

    // ---- Fight start -------------------------------------------------------------------------------------------

    /// <summary>
    /// Once per simulated fight, before the first attack. Not a Start of Combat trigger (those already resolved before
    /// capture): use it to set up what lasts "for the rest of this combat" (e.g. a buff for minions summoned later).
    /// </summary>
    public virtual void OnFightStart(Fight fight, Unit self) { }

    // ---- Attacks -----------------------------------------------------------------------------------------------

    /// <summary>This minion attacks <paramref name="target"/>, before damage: Rally and "whenever this attacks".</summary>
    public virtual void OnAttack(Fight fight, Unit self, Unit target) { }

    /// <summary>A friendly minion attacks, before damage. Also called with <paramref name="attacker"/> == self; check it for "another".</summary>
    public virtual void OnFriendlyAttack(Fight fight, Unit self, Unit attacker, Unit target) { }

    /// <summary>A friendly minion (possibly self) is attacked, before damage.</summary>
    public virtual void OnFriendlyAttacked(Fight fight, Unit self, Unit defender, Unit attacker) { }

    /// <summary>During this minion's combat damage, after the main exchange: deal extra simultaneous damage here (cleave).</summary>
    public virtual void OnAttackDamage(Fight fight, Unit self, Unit target) { }

    /// <summary>After this minion attacked, before the death phase (self and target may be mortally wounded).</summary>
    public virtual void AfterAttack(Fight fight, Unit self, Unit target, AttackOutcome outcome) { }

    /// <summary>After a friendly minion (possibly self) attacked, before the death phase.</summary>
    public virtual void AfterFriendlyAttack(Fight fight, Unit self, Unit attacker, Unit target, AttackOutcome outcome) { }

    // ---- Damage ------------------------------------------------------------------------------------------------

    /// <summary>Whenever this minion takes damage (after Divine Shield; not called when the shield absorbed it).</summary>
    public virtual void OnDamaged(Fight fight, Unit self, Unit? source, int amount) { }

    /// <summary>After a friendly minion (possibly self) dealt damage to any minion.</summary>
    public virtual void AfterFriendlyDealtDamage(Fight fight, Unit self, Unit dealer, Unit victim, int amount) { }

    /// <summary>This minion's damage (attack or effect) or destroy effect killed <paramref name="victim"/>.</summary>
    public virtual void AfterKill(Fight fight, Unit self, Unit victim) { }

    /// <summary>A friendly minion (possibly self) lost Divine Shield.</summary>
    public virtual void OnFriendlyShieldLost(Fight fight, Unit self, Unit unit) { }

    // ---- Deaths ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Deathrattle. <paramref name="self"/> is already off the board; <see cref="Fight.SummonAt(Unit, SimMinion)"/> summons
    /// at its old slot. Runs once per trigger; the engine repeats it for multipliers (<see cref="ExtraDeathrattles"/>).
    /// Also called by <see cref="Fight.TriggerDeathrattles"/> on a living minion (then summons go to its right).
    /// </summary>
    public virtual void Deathrattle(Fight fight, Unit self) { }

    /// <summary>Extra times friendly deathrattles trigger while this minion is alive (Titus Rivendare: 1, golden 2).</summary>
    public virtual int ExtraDeathrattles(Fight fight, Unit self) => 0;

    /// <summary>After one friendly deathrattle trigger resolved (per trigger, multipliers included).</summary>
    public virtual void OnFriendlyDeathrattle(Fight fight, Unit self, Unit source) { }

    /// <summary>After a friendly minion died (its deathrattles and Reborn have resolved): Avenge, "after a friendly X dies".</summary>
    public virtual void AfterFriendlyDeath(Fight fight, Unit self, Unit dead) { }

    /// <summary>After an enemy minion died.</summary>
    public virtual void AfterEnemyDeath(Fight fight, Unit self, Unit dead) { }

    // ---- Summons -----------------------------------------------------------------------------------------------

    /// <summary>This minion was just summoned in combat (token, reborn copy, ...), before other minions react.</summary>
    public virtual void OnSummoned(Fight fight, Unit self) { }

    /// <summary>"Whenever you summon": a friendly minion other than self was summoned. Runs before the "after" hooks.</summary>
    public virtual void WheneverFriendlySummoned(Fight fight, Unit self, Unit summoned) { }

    /// <summary>"After you summon": a friendly minion other than self was summoned.</summary>
    public virtual void AfterFriendlySummoned(Fight fight, Unit self, Unit summoned) { }

    /// <summary>After a friendly minion was Reborn (<paramref name="reborn"/> is the new copy; its origin is <see cref="Unit.RebornFrom"/>).</summary>
    public virtual void AfterFriendlyReborn(Fight fight, Unit self, Unit reborn) { }

    /// <summary>After the owner cast a spell in combat (see <see cref="Fight.CastSpell"/>).</summary>
    public virtual void AfterFriendlySpellCast(Fight fight, Unit self, string spellCardId, Unit? target) { }

    // ---- Ongoing -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Static aura from self onto <paramref name="other"/> (a friendly minion, possibly self). Recomputed whenever the board
    /// changes; the captured stats already include the auras present at capture, and the engine applies only changes.
    /// </summary>
    public virtual StatDelta AuraFor(Fight fight, Unit self, Unit other) => default;

    /// <summary>Re-evaluates state-dependent keywords or thresholds (e.g. "has Taunt while it has Divine Shield").</summary>
    public virtual void Refresh(Fight fight, Unit self) { }

    private static Hook ScanHooks(Type type)
    {
        var hooks = Hook.None;
        foreach (var (name, hook) in HookNames)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            if (method is not null && method.DeclaringType != typeof(CombatEffect))
            {
                hooks |= hook;
            }
        }

        return hooks;
    }

    private static readonly (string Name, Hook Hook)[] HookNames =
    [
        (nameof(OnFightStart), Hook.FightStart),
        (nameof(OnAttack), Hook.Attack),
        (nameof(OnFriendlyAttack), Hook.FriendlyAttack),
        (nameof(OnFriendlyAttacked), Hook.FriendlyAttacked),
        (nameof(OnAttackDamage), Hook.AttackDamage),
        (nameof(AfterAttack), Hook.AfterAttack),
        (nameof(AfterFriendlyAttack), Hook.AfterFriendlyAttack),
        (nameof(OnDamaged), Hook.Damaged),
        (nameof(AfterFriendlyDealtDamage), Hook.FriendlyDealtDamage),
        (nameof(AfterKill), Hook.Kill),
        (nameof(OnFriendlyShieldLost), Hook.ShieldLost),
        (nameof(Deathrattle), Hook.Deathrattle),
        (nameof(ExtraDeathrattles), Hook.ExtraDeathrattles),
        (nameof(OnFriendlyDeathrattle), Hook.FriendlyDeathrattle),
        (nameof(AfterFriendlyDeath), Hook.FriendlyDeath),
        (nameof(AfterEnemyDeath), Hook.EnemyDeath),
        (nameof(OnSummoned), Hook.Summoned),
        (nameof(WheneverFriendlySummoned), Hook.WheneverSummon),
        (nameof(AfterFriendlySummoned), Hook.AfterSummon),
        (nameof(AfterFriendlyReborn), Hook.Reborn),
        (nameof(AfterFriendlySpellCast), Hook.SpellCast),
        (nameof(AuraFor), Hook.Aura),
        (nameof(Refresh), Hook.Refresh),
    ];
}

/// <summary>Avenge (X): counts friendly deaths while this minion is alive and fires every X of them.</summary>
public abstract class AvengeEffect : CombatEffect
{
    /// <summary>The X of Avenge (X).</summary>
    protected abstract int Threshold(Unit self);

    protected abstract void Avenge(Fight fight, Unit self);

    public override void AfterFriendlyDeath(Fight fight, Unit self, Unit dead)
    {
        if (++self.AvengeCount >= Math.Max(1, Threshold(self)))
        {
            self.AvengeCount = 0;
            Avenge(fight, self);
        }
    }
}

/// <summary>Which hooks an effect overrides; the engine skips the rest.</summary>
[Flags]
internal enum Hook
{
    None = 0,
    FightStart = 1 << 0,
    Attack = 1 << 1,
    FriendlyAttack = 1 << 2,
    FriendlyAttacked = 1 << 3,
    AttackDamage = 1 << 4,
    AfterAttack = 1 << 5,
    AfterFriendlyAttack = 1 << 6,
    Damaged = 1 << 7,
    FriendlyDealtDamage = 1 << 8,
    Kill = 1 << 9,
    ShieldLost = 1 << 10,
    Deathrattle = 1 << 11,
    ExtraDeathrattles = 1 << 12,
    FriendlyDeathrattle = 1 << 13,
    FriendlyDeath = 1 << 14,
    EnemyDeath = 1 << 15,
    Summoned = 1 << 16,
    WheneverSummon = 1 << 17,
    AfterSummon = 1 << 18,
    Reborn = 1 << 19,
    SpellCast = 1 << 20,
    Aura = 1 << 21,
    Refresh = 1 << 22,
}
