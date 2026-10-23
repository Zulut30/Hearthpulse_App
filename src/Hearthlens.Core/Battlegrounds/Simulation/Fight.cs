using Hearthlens.Core.Battlegrounds.Effects;

namespace Hearthlens.Core.Battlegrounds;

/// <summary>
/// One simulated Battlegrounds fight and the API card effects use: board queries, random picks (seeded), summons,
/// buffs, damage, destroy, extra attacks and deathrattle triggers.
/// </summary>
/// <remarks>
/// Rules and the assumptions behind them (unverified against real logs where noted):
/// <list type="bullet">
/// <item>Sides alternate attacks. A side's next attacker is the first minion with Attack at or right of its "gap",
/// which sits just right of that side's last attacker (wrapping to the left-most minion). Minions summoned left of the
/// gap (for example in the slot of an attacker that died) wait for the next round; minions summoned right of it, such as
/// a reborn copy of the minion that was due to attack next, attack in turn. This reproduces the simulator's previous
/// behaviour; it is not verified against Power.log.</item>
/// <item>An attack: "when this attacks" hooks (Rally) and "whenever a friendly minion attacks / is attacked" hooks run
/// before damage, then a death phase. If the attacker or the target died, the attack is cancelled. Otherwise damage
/// is simultaneous; damage-triggered hooks and after-attack hooks run while dead minions are still on the board
/// (mortally wounded), then the death phase.</item>
/// <item>Death phase: all dead minions are removed at once (the attacking side first, then the other, each left to
/// right). Then, per dead minion in that order: its deathrattles (× 1 + extra triggers from living minions such as
/// Titus Rivendare and from the side), then its Reborn. Then "after a friendly/enemy minion dies" hooks (Avenge)
/// for each death, auras are refreshed, and the phase repeats while minions keep dying.</item>
/// <item>Summons from a dead minion appear in its slot, left to right in the order they are made, the reborn copy
/// after its deathrattle summons. A side holds at most 7 minions; summons beyond that are lost.</item>
/// <item>Reborn brings back the card's printed Attack and keywords with 1 Health, golden if it was golden, without
/// buffs or enchantments.</item>
/// <item>"Attacks immediately" (<see cref="AttackSoon"/>) happens right after the attack in progress; Windfury's
/// second attack comes after it.</item>
/// </list>
/// </remarks>
public sealed class Fight
{
    private const int MaxAttacks = 400;
    private const int MaxDepth = 24;
    private const int MaxDeathRounds = 64;

    private const Hook DamageHooks = Hook.Damaged | Hook.FriendlyDealtDamage | Hook.Kill | Hook.ShieldLost;

    private readonly EffectRegistry _registry;
    private readonly List<string>? _log;
    private readonly List<Unit> _dead = new();
    // One observer buffer per nesting depth, so hooks can be dispatched without allocating.
    private readonly Unit[]?[] _buffers = new Unit[MaxDepth + 8][];
    private Queue<Unit>? _queuedAttacks;
    private List<DamageEvent>? _batch;
    private int _batchDepth;
    private bool _inDeathPhase;
    private int _depth;
    private int _attacks;

    internal Fight(SimSide ours, SimSide theirs, EffectRegistry registry, Random random, bool trace, PreparedEffects? prepared = null)
    {
        _registry = registry;
        Random = random;
        _log = trace ? new List<string>() : null;
        Ours = new Side(this, 0, ours);
        Theirs = new Side(this, 1, theirs);
        Ours.Enemy = Theirs;
        Theirs.Enemy = Ours;
        Attacking = Ours;
        prepared ??= PreparedEffects.For(ours, theirs, registry);
        Populate(Ours, ours, prepared.Ours, prepared.OurExtras);
        Populate(Theirs, theirs, prepared.Theirs, prepared.TheirExtras);
    }

    /// <summary>Effects of the captured minions and player-level cards, looked up once for many fights.</summary>
    internal sealed record PreparedEffects(CombatEffect[][] Ours, CombatEffect[][] Theirs, CombatEffect?[] OurExtras, CombatEffect?[] TheirExtras)
    {
        public static PreparedEffects For(SimSide ours, SimSide theirs, EffectRegistry registry) => new(
            ours.Minions.Select(registry.EffectsFor).ToArray(),
            theirs.Minions.Select(registry.EffectsFor).ToArray(),
            ours.Extras.Select(id => registry.Get(id, golden: false)).ToArray(),
            theirs.Extras.Select(id => registry.Get(id, golden: false)).ToArray());
    }

    /// <summary>The local player's side.</summary>
    public Side Ours { get; }

    /// <summary>The opponent's side.</summary>
    public Side Theirs { get; }

    /// <summary>
    /// The fight's random source. Use only <see cref="System.Random.Next(int)"/> so tests can script picks; prefer the
    /// helpers (<see cref="RandomOf"/>, <see cref="RandomFriendly"/>...).
    /// </summary>
    public Random Random { get; }

    /// <summary>The side whose attack is being resolved; its deaths resolve first.</summary>
    public Side Attacking { get; private set; }

    public bool Tracing => _log is not null;

    public IReadOnlyList<string> Log => (IReadOnlyList<string>?)_log ?? Array.Empty<string>();

    /// <summary>Adds a line to the fight log (tests); free when tracing is off. Guard costly messages with <see cref="Tracing"/>.</summary>
    public void Trace(string message) => _log?.Add(message);

    // ================================================================================================ fight loop

    /// <returns>+1 we win, -1 we lose, 0 tie.</returns>
    internal int Run(bool? firstAttackerIsOurs, int firstAttackerEntityId)
    {
        var side = firstAttackerIsOurs switch
        {
            true => Ours,
            false => Theirs,
            // The side with more minions attacks first; a coin flip on equal counts.
            null => Ours.Count > Theirs.Count ? Ours : Theirs.Count > Ours.Count ? Theirs : Random.Next(2) == 0 ? Ours : Theirs,
        };

        if (firstAttackerEntityId != 0)
        {
            for (var i = 0; i < side.Count; i++)
            {
                if (side.Minions[i].EntityId == firstAttackerEntityId)
                {
                    side.Gap = i;
                    break;
                }
            }
        }

        Attacking = side;
        StartFight();

        while (Ours.Count > 0 && Theirs.Count > 0 && _attacks < MaxAttacks)
        {
            Attacking = side;
            var attacker = side.NextAttacker();
            if (attacker is null)
            {
                // This side cannot attack; if neither can, the fight stalls into a tie.
                if (side.Enemy.NextAttacker() is null)
                {
                    break;
                }

                side = side.Enemy;
                continue;
            }

            side.Gap = side.IndexOf(attacker) + 1;
            for (var swing = 0; swing < attacker.AttacksPerTurn && attacker.IsAlive && side.Enemy.Count > 0 && _attacks < MaxAttacks; swing++)
            {
                Attack(attacker, null);
                RunQueuedAttacks();
            }

            side = side.Enemy;
        }

        var outcome = Ours.Count > 0 && Theirs.Count == 0 ? 1 : Theirs.Count > 0 && Ours.Count == 0 ? -1 : 0;
        Trace(outcome switch { > 0 => "result: win", < 0 => "result: loss", _ => "result: tie" });
        return outcome;
    }

    private static void Populate(Side side, SimSide info, CombatEffect[][] effects, CombatEffect?[] extras)
    {
        for (var i = 0; i < info.Minions.Count; i++)
        {
            side.AddInitial(new Unit(info.Minions[i], side, effects[i]));
        }

        for (var i = 0; i < info.Extras.Count; i++)
        {
            if (extras[i] is { } effect)
            {
                side.AddExtra(new Unit(SimMinion.Create(info.Extras[i], 0, 0), side, [effect], extra: true));
            }
        }
    }

    private Unit CreateUnit(SimMinion spec, Side side) => new(spec, side, _registry.EffectsFor(spec));

    private void StartFight()
    {
        StartSide(Ours);
        StartSide(Theirs);
        BaselineAuras(Ours);
        BaselineAuras(Theirs);

        RefreshAuras();
        ResolveDeaths();
        RunQueuedAttacks();
    }

    private void StartSide(Side side)
    {
        if ((side.Hooks & Hook.FightStart) == 0)
        {
            return;
        }

        _depth++;
        try
        {
            var observers = Snapshot(side, out var count);
            for (var i = 0; i < count; i++)
            {
                var unit = observers[i];
                foreach (var effect in unit.Effects)
                {
                    if ((effect.Hooks & Hook.FightStart) != 0)
                    {
                        effect.OnFightStart(this, unit);
                    }
                }
            }
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>Captured stats already include the auras present at capture: record them without applying.</summary>
    private void BaselineAuras(Side side)
    {
        if ((side.Hooks & Hook.Aura) == 0)
        {
            return;
        }

        foreach (var unit in side.Minions)
        {
            unit.AppliedAura = AuraTotal(side, unit);
            side.HasAppliedAuras |= !unit.AppliedAura.IsZero;
        }

        side.AuraDirty = false;
    }

    /// <summary>Board and extra units of a side at this moment, in a buffer owned by the current nesting depth.</summary>
    private Unit[] Snapshot(Side side, out int count)
    {
        count = side.Count + side.Extras.Count;
        if (_depth >= _buffers.Length)
        {
            var fresh = new Unit[count];
            side.CopyObservers(fresh);
            return fresh;
        }

        var buffer = _buffers[_depth];
        if (buffer is null || buffer.Length < count)
        {
            buffer = _buffers[_depth] = new Unit[Math.Max(16, count)];
        }

        side.CopyObservers(buffer);
        return buffer;
    }

    // ================================================================================================ attacks

    private void Attack(Unit attacker, Unit? forcedTarget)
    {
        var defenders = attacker.Enemy;
        var target = forcedTarget is { IsAlive: true } && forcedTarget.Side == defenders ? forcedTarget : PickTarget(defenders);
        if (target is null || _depth >= MaxDepth)
        {
            return;
        }

        var previous = Attacking;
        Attacking = attacker.Side;
        _depth++;
        _attacks++;
        try
        {
            attacker.Stealth = false;
            if (Tracing) Trace($"{Name(attacker)} attacks {Name(target)}");

            // Attack declared: Rally / "whenever this attacks", then friendly and defending-side triggers, before damage.
            if ((attacker.Hooks & Hook.Attack) != 0)
            {
                foreach (var effect in attacker.Effects)
                {
                    if ((effect.Hooks & Hook.Attack) != 0)
                    {
                        effect.OnAttack(this, attacker, target);
                    }
                }
            }

            Raise(attacker.Side, Hook.FriendlyAttack, attacker, target);
            Raise(defenders, Hook.FriendlyAttacked, target, attacker);
            RefreshAuras();
            ResolveDeaths();
            if (!attacker.IsAlive || !target.IsAlive)
            {
                // The attacker or its target died before damage (e.g. to a Rally): the attack does not happen.
                if (Tracing) Trace("attack cancelled");
                return;
            }

            // Combat damage, simultaneous: both amounts are read before either is dealt.
            var toTarget = attacker.Attack;
            var toAttacker = target.Attack;
            var targetHealth = target.Health;
            BeginDamageBatch();
            var dealt = DamageCore(attacker, target, toTarget);
            DamageCore(target, attacker, toAttacker);
            if ((attacker.Hooks & Hook.AttackDamage) != 0)
            {
                foreach (var effect in attacker.Effects)
                {
                    if ((effect.Hooks & Hook.AttackDamage) != 0)
                    {
                        effect.OnAttackDamage(this, attacker, target);
                    }
                }
            }

            EndDamageBatch();

            var killed = target.IsDying;
            var outcome = new AttackOutcome(dealt, killed, killed ? Math.Max(0, dealt - Math.Max(0, targetHealth)) : 0);
            if ((attacker.Hooks & Hook.AfterAttack) != 0 && attacker.IsOnBoard)
            {
                foreach (var effect in attacker.Effects)
                {
                    if ((effect.Hooks & Hook.AfterAttack) != 0)
                    {
                        effect.AfterAttack(this, attacker, target, outcome);
                    }
                }
            }

            Raise(attacker.Side, Hook.AfterFriendlyAttack, attacker, target, outcome: outcome);
            RefreshAuras();
            ResolveDeaths();
        }
        finally
        {
            _depth--;
            Attacking = previous;
        }
    }

    /// <summary>Random target: Taunt minions first; Stealth minions only when nothing else can be attacked.</summary>
    private Unit? PickTarget(Side side)
    {
        int visible = 0, visibleTaunts = 0, alive = 0, taunts = 0;
        foreach (var unit in side.Minions)
        {
            if (!unit.IsAlive)
            {
                continue;
            }

            alive++;
            if (unit.Taunt) taunts++;
            if (!unit.Stealth)
            {
                visible++;
                if (unit.Taunt) visibleTaunts++;
            }
        }

        if (alive == 0)
        {
            return null;
        }

        // 0: visible taunts, 1: visible, 2: any taunt, 3: any.
        var mode = visible > 0 ? (visibleTaunts > 0 ? 0 : 1) : (taunts > 0 ? 2 : 3);
        var count = mode switch { 0 => visibleTaunts, 1 => visible, 2 => taunts, _ => alive };
        var pick = Random.Next(count);
        foreach (var unit in side.Minions)
        {
            if (!unit.IsAlive)
            {
                continue;
            }

            var eligible = mode switch
            {
                0 => !unit.Stealth && unit.Taunt,
                1 => !unit.Stealth,
                2 => unit.Taunt,
                _ => true,
            };
            if (eligible && pick-- == 0)
            {
                return unit;
            }
        }

        return null;
    }

    /// <summary>Makes <paramref name="attacker"/> attack right now, out of turn order (e.g. a Golem that "attacks the target first").</summary>
    /// <param name="target">The target, or null for a normal random target.</param>
    public void AttackNow(Unit attacker, Unit? target = null)
    {
        if (attacker.IsAlive && _attacks < MaxAttacks)
        {
            Attack(attacker, target);
        }
    }

    /// <summary>"Attacks immediately": queues an out-of-turn attack that happens right after the current attack finishes.</summary>
    public void AttackSoon(Unit attacker) => (_queuedAttacks ??= new Queue<Unit>()).Enqueue(attacker);

    private void RunQueuedAttacks()
    {
        while (_queuedAttacks is { Count: > 0 } && Ours.Count > 0 && Theirs.Count > 0 && _attacks < MaxAttacks)
        {
            var unit = _queuedAttacks.Dequeue();
            if (unit.IsAlive)
            {
                Attack(unit, null);
            }
        }

        _queuedAttacks?.Clear();
    }

    // ================================================================================================ damage

    /// <summary>
    /// Deals effect damage (Divine Shield, Poisonous and Venomous of the source apply) and fires damage hooks.
    /// The target dies at the next death phase.
    /// </summary>
    /// <returns>Damage dealt (0 when Divine Shield absorbed it or the target is not on the board).</returns>
    public int DealDamage(Unit? source, Unit target, int amount) => DamageCore(source, target, amount);

    /// <summary>Destroys a minion; it dies at the next death phase.</summary>
    public void Destroy(Unit target, Unit? source = null)
    {
        if (!target.IsOnBoard || target.IsDying)
        {
            return;
        }

        target.MarkedForDeath = true;
        target.Killer ??= source;
        if (Tracing) Trace($"{Name(target)} is destroyed");
        if (source is not null)
        {
            Fire(new DamageEvent(source, target, 0, Killed: true, ShieldLost: false));
        }
    }

    private int DamageCore(Unit? source, Unit target, int amount)
    {
        if (amount <= 0 || !target.IsOnBoard)
        {
            return 0;
        }

        if (target.DivineShield)
        {
            target.DivineShield = false;
            if (Tracing) Trace($"{Name(target)} loses Divine Shield");
            Emit(new DamageEvent(source, target, 0, Killed: false, ShieldLost: true));
            return 0;
        }

        var wasAlive = !target.IsDying;
        target.Health -= amount;
        if (source is not null && (source.Poisonous || source.Venomous))
        {
            target.Health = Math.Min(target.Health, 0);
            source.Venomous = false;
        }

        var killed = wasAlive && target.IsDying;
        if (killed && source is not null)
        {
            target.Killer = source;
        }

        if (Tracing) Trace($"{Name(target)} takes {amount}{(source is null ? string.Empty : " from " + Name(source))}");
        Emit(new DamageEvent(source, target, amount, killed, ShieldLost: false));
        return amount;
    }

    private void BeginDamageBatch() => _batchDepth++;

    private void EndDamageBatch()
    {
        if (--_batchDepth > 0 || _batch is not { Count: > 0 } events)
        {
            return;
        }

        // Hooks may start a nested attack with its own batch: it gets a fresh list.
        _batch = null;
        foreach (var e in events)
        {
            Fire(e);
        }

        events.Clear();
        _batch ??= events;
    }

    private void Emit(DamageEvent e)
    {
        if (((Ours.Hooks | Theirs.Hooks) & DamageHooks) == 0)
        {
            return;
        }

        if (_batchDepth > 0)
        {
            (_batch ??= new List<DamageEvent>()).Add(e);
        }
        else
        {
            Fire(e);
        }
    }

    private void Fire(DamageEvent e)
    {
        if (e.ShieldLost)
        {
            Raise(e.Target.Side, Hook.ShieldLost, e.Target);
            return;
        }

        if (e.Amount > 0 && (e.Target.Hooks & Hook.Damaged) != 0)
        {
            foreach (var effect in e.Target.Effects)
            {
                if ((effect.Hooks & Hook.Damaged) != 0)
                {
                    effect.OnDamaged(this, e.Target, e.Source, e.Amount);
                }
            }
        }

        if (e.Source is not { } source)
        {
            return;
        }

        if (e.Amount > 0)
        {
            Raise(source.Side, Hook.FriendlyDealtDamage, source, e.Target, e.Amount);
        }

        if (e.Killed && (source.Hooks & Hook.Kill) != 0)
        {
            foreach (var effect in source.Effects)
            {
                if ((effect.Hooks & Hook.Kill) != 0)
                {
                    effect.AfterKill(this, source, e.Target);
                }
            }
        }
    }

    private readonly record struct DamageEvent(Unit? Source, Unit Target, int Amount, bool Killed, bool ShieldLost);

    // ================================================================================================ deaths

    /// <summary>Runs pending death phases now (the engine also does this after every step).</summary>
    public void ResolveDeaths()
    {
        if (_inDeathPhase)
        {
            // The outer death phase picks these deaths up in its next round.
            return;
        }

        if (!AnyDying(Ours) && !AnyDying(Theirs))
        {
            return;
        }

        _inDeathPhase = true;
        try
        {
            var dead = _dead;
            for (var round = 0; round < MaxDeathRounds; round++)
            {
                dead.Clear();
                CollectDead(Attacking, dead);
                CollectDead(Attacking.Enemy, dead);
                if (dead.Count == 0)
                {
                    break;
                }

                // Remember where each one stood, then take them all off the board at once.
                foreach (var unit in dead)
                {
                    var side = unit.Side;
                    var index = side.IndexOf(unit);
                    unit.DeathIndex = index;
                    unit.DiedLeftOfGap = index < side.Gap;
                    unit.DeathAnchor = null;
                    for (var i = index + 1; i < side.Count; i++)
                    {
                        if (!side.Minions[i].IsDying)
                        {
                            unit.DeathAnchor = side.Minions[i];
                            break;
                        }
                    }
                }

                foreach (var unit in dead)
                {
                    unit.Side.Remove(unit);
                }

                foreach (var unit in dead)
                {
                    if (Tracing) Trace($"{Name(unit)} dies");
                    RunDeathrattles(unit, DeathrattleTriggers(unit.Side));
                    if (unit.Reborn)
                    {
                        SummonReborn(unit);
                    }
                }

                foreach (var unit in dead)
                {
                    Raise(unit.Side, Hook.FriendlyDeath, unit);
                    Raise(unit.Enemy, Hook.EnemyDeath, unit);
                }

                RefreshAuras();
            }
        }
        finally
        {
            _dead.Clear();
            _inDeathPhase = false;
        }
    }

    private static bool AnyDying(Side side)
    {
        foreach (var unit in side.Minions)
        {
            if (unit.IsDying)
            {
                return true;
            }
        }

        return false;
    }

    private static void CollectDead(Side side, List<Unit> dead)
    {
        foreach (var unit in side.Minions)
        {
            if (unit.IsDying)
            {
                dead.Add(unit);
            }
        }
    }

    /// <summary>How many times a deathrattle of this side triggers: 1 + extra triggers from living minions and the side.</summary>
    public int DeathrattleTriggers(Side side)
    {
        var times = 1 + Math.Max(0, side.ExtraDeathrattles);
        if ((side.Hooks & Hook.ExtraDeathrattles) == 0)
        {
            return times;
        }

        _depth++;
        try
        {
            var observers = Snapshot(side, out var count);
            for (var i = 0; i < count; i++)
            {
                var unit = observers[i];
                if ((unit.Hooks & Hook.ExtraDeathrattles) == 0 || (!unit.IsAlive && !unit.IsExtra))
                {
                    continue;
                }

                foreach (var effect in unit.Effects)
                {
                    if ((effect.Hooks & Hook.ExtraDeathrattles) != 0)
                    {
                        times += Math.Max(0, effect.ExtraDeathrattles(this, unit));
                    }
                }
            }
        }
        finally
        {
            _depth--;
        }

        return times;
    }

    /// <summary>
    /// Triggers a minion's deathrattles without it dying (e.g. "trigger your left-most Deathrattle"); summons go to its right.
    /// </summary>
    /// <param name="withMultiplier">Also apply extra triggers (Titus Rivendare and similar).</param>
    public void TriggerDeathrattles(Unit unit, int times = 1, bool withMultiplier = true) =>
        RunDeathrattles(unit, times * (withMultiplier ? DeathrattleTriggers(unit.Side) : 1));

    private void RunDeathrattles(Unit unit, int times)
    {
        if ((unit.Hooks & Hook.Deathrattle) == 0 || _depth >= MaxDepth)
        {
            return;
        }

        _depth++;
        try
        {
            var effects = unit.Effects;
            for (var t = 0; t < times; t++)
            {
                foreach (var effect in effects)
                {
                    if ((effect.Hooks & Hook.Deathrattle) == 0)
                    {
                        continue;
                    }

                    if (Tracing) Trace($"deathrattle of {Name(unit)}");
                    effect.Deathrattle(this, unit);
                    unit.Side.DeathrattlesTriggered++;
                    Raise(unit.Side, Hook.FriendlyDeathrattle, unit);
                }
            }
        }
        finally
        {
            _depth--;
        }
    }

    private void SummonReborn(Unit dead)
    {
        var copy = SummonCore(dead.Side, dead.Spec.RebornCopy(), SlotOf(dead), dead.DiedLeftOfGap, rebornFrom: dead);
        if (copy is not null)
        {
            Raise(dead.Side, Hook.Reborn, copy, exclude: copy);
        }
    }

    // ================================================================================================ summons

    /// <summary>Summons at a board index (clamped). Returns null when the side already has 7 minions.</summary>
    public Unit? Summon(Side side, SimMinion minion, int index) => SummonCore(side, minion, index, leftOfGap: false, rebornFrom: null);

    /// <summary>Summons a minion with these printed stats at <paramref name="slot"/> (-1: right end).</summary>
    public Unit? Summon(Side side, string cardId, int attack, int health, Keyword keywords = Keyword.None, int slot = -1,
                        Tribe tribes = Tribe.None, bool golden = false) =>
        Summon(side, SimMinion.Create(cardId, attack, health, tribes, keywords, golden), slot < 0 ? side.Count : slot);

    /// <summary>
    /// Summons where <paramref name="place"/> is: in its old slot when it has died (deathrattles), else to its right.
    /// Returns null when the side already has 7 minions.
    /// </summary>
    public Unit? SummonAt(Unit place, SimMinion minion) =>
        SummonCore(place.Side, minion, SlotOf(place), !place.IsOnBoard && place.DiedLeftOfGap, rebornFrom: null);

    /// <summary>Summons <paramref name="count"/> copies left to right where <paramref name="place"/> is (see <see cref="SummonAt(Unit, SimMinion)"/>).</summary>
    public IReadOnlyList<Unit> SummonAt(Unit place, SimMinion minion, int count)
    {
        var summoned = new List<Unit>(count);
        var anchor = place;
        for (var i = 0; i < count; i++)
        {
            var unit = place.IsOnBoard
                ? SummonCore(place.Side, minion, anchor.IsOnBoard ? anchor.Position + 1 : SlotOf(place), false, null)
                : SummonAt(place, minion);
            if (unit is null)
            {
                break;
            }

            summoned.Add(unit);
            anchor = unit;
        }

        return summoned;
    }

    private static int SlotOf(Unit place)
    {
        if (place.IsOnBoard)
        {
            return place.Position + 1;
        }

        if (place.DeathAnchor is null)
        {
            // Nothing alive stood to its right when it died: append, so every further summon from this slot (and the
            // reborn copy) lands right of the previous one. A fixed index would put each new summon left of the last.
            return place.Side.Count;
        }

        return place.DeathAnchor is { IsOnBoard: true } anchor && anchor.Side == place.Side
            ? anchor.Position
            : Math.Min(place.DeathIndex < 0 ? place.Side.Count : place.DeathIndex, place.Side.Count);
    }

    private Unit? SummonCore(Side side, SimMinion minion, int index, bool leftOfGap, Unit? rebornFrom)
    {
        if (side.IsFull || _depth >= MaxDepth)
        {
            if (Tracing) Trace($"no room to summon {minion.CardId} for {(side.IsOpponent ? "them" : "us")}");
            return null;
        }

        var unit = CreateUnit(minion, side);
        unit.RebornFrom = rebornFrom;
        side.Insert(index, unit, leftOfGap);
        side.ApplySummonBuffs(unit);
        if (Tracing) Trace($"{(rebornFrom is null ? "summoned" : "reborn")} {Name(unit)} at {unit.Position}");

        _depth++;
        try
        {
            if ((unit.Hooks & Hook.Summoned) != 0)
            {
                foreach (var effect in unit.Effects)
                {
                    if ((effect.Hooks & Hook.Summoned) != 0)
                    {
                        effect.OnSummoned(this, unit);
                    }
                }
            }

            Raise(side, Hook.WheneverSummon, unit, exclude: unit);
            Raise(side, Hook.AfterSummon, unit, exclude: unit);
            RefreshAuras();
        }
        finally
        {
            _depth--;
        }

        return unit;
    }

    // ================================================================================================ stats and keywords

    /// <summary>Gives +Attack/+Health (negative values reduce; Attack never goes below 0).</summary>
    public void Buff(Unit target, int attack, int health)
    {
        target.Attack = Math.Max(0, target.Attack + attack);
        target.Health += health;
        target.MaxHealth += health;
        RefreshUnit(target);
    }

    /// <summary>Sets Attack and Health (and maximum Health).</summary>
    public void SetStats(Unit target, int attack, int health)
    {
        target.Attack = Math.Max(0, attack);
        target.Health = health;
        target.MaxHealth = health;
        RefreshUnit(target);
    }

    public void GiveKeywords(Unit target, Keyword keywords)
    {
        if ((keywords & Keyword.Taunt) != 0) target.Taunt = true;
        if ((keywords & Keyword.DivineShield) != 0) target.DivineShield = true;
        if ((keywords & Keyword.Poisonous) != 0) target.Poisonous = true;
        if ((keywords & Keyword.Venomous) != 0) target.Venomous = true;
        if ((keywords & Keyword.Reborn) != 0) target.Reborn = true;
        if ((keywords & Keyword.Stealth) != 0) target.Stealth = true;
        if ((keywords & Keyword.Windfury) != 0) target.AttacksPerTurn = Math.Max(target.AttacksPerTurn, 2);
        if ((keywords & Keyword.MegaWindfury) != 0) target.AttacksPerTurn = Math.Max(target.AttacksPerTurn, 4);
        RefreshUnit(target);
    }

    public void RemoveKeywords(Unit target, Keyword keywords)
    {
        if ((keywords & Keyword.Taunt) != 0) target.Taunt = false;
        if ((keywords & Keyword.DivineShield) != 0) target.DivineShield = false;
        if ((keywords & Keyword.Poisonous) != 0) target.Poisonous = false;
        if ((keywords & Keyword.Venomous) != 0) target.Venomous = false;
        if ((keywords & Keyword.Reborn) != 0) target.Reborn = false;
        if ((keywords & Keyword.Stealth) != 0) target.Stealth = false;
        if ((keywords & (Keyword.Windfury | Keyword.MegaWindfury)) != 0) target.AttacksPerTurn = 1;
        RefreshUnit(target);
    }

    private void RefreshUnit(Unit unit)
    {
        if ((unit.Hooks & Hook.Refresh) == 0)
        {
            return;
        }

        foreach (var effect in unit.Effects)
        {
            if ((effect.Hooks & Hook.Refresh) != 0)
            {
                effect.Refresh(this, unit);
            }
        }
    }

    /// <summary>Re-applies auras (<see cref="CombatEffect.AuraFor"/>) and <see cref="CombatEffect.Refresh"/> hooks on both sides.</summary>
    public void RefreshAuras()
    {
        RefreshSide(Ours);
        RefreshSide(Theirs);
    }

    private void RefreshSide(Side side)
    {
        var hooks = side.Hooks;
        if (side.AuraDirty && ((hooks & Hook.Aura) != 0 || side.HasAppliedAuras))
        {
            side.AuraDirty = false;
            var any = false;
            foreach (var unit in side.Minions)
            {
                var total = AuraTotal(side, unit);
                var delta = total - unit.AppliedAura;
                if (!delta.IsZero)
                {
                    unit.Attack = Math.Max(0, unit.Attack + delta.Attack);
                    unit.MaxHealth += delta.Health;
                    // A removed Health aura lowers current Health only down to the new maximum.
                    unit.Health = delta.Health > 0 ? unit.Health + delta.Health : Math.Min(unit.Health, unit.MaxHealth);
                }

                unit.AppliedAura = total;
                any |= !total.IsZero;
            }

            side.HasAppliedAuras = any;
        }

        if ((hooks & Hook.Refresh) != 0)
        {
            _depth++;
            try
            {
                var observers = Snapshot(side, out var count);
                for (var i = 0; i < count; i++)
                {
                    RefreshUnit(observers[i]);
                }
            }
            finally
            {
                _depth--;
            }
        }
    }

    private StatDelta AuraTotal(Side side, Unit target)
    {
        StatDelta total = default;
        foreach (var source in side.Minions)
        {
            total += AuraFrom(source, target);
        }

        foreach (var source in side.Extras)
        {
            total += AuraFrom(source, target);
        }

        return total;
    }

    private StatDelta AuraFrom(Unit source, Unit target)
    {
        StatDelta total = default;
        if ((source.Hooks & Hook.Aura) == 0)
        {
            return total;
        }

        foreach (var effect in source.Effects)
        {
            if ((effect.Hooks & Hook.Aura) != 0)
            {
                total += effect.AuraFor(this, source, target);
            }
        }

        return total;
    }

    // ================================================================================================ spells

    /// <summary>
    /// A spell cast in combat by <paramref name="caster"/> (e.g. a Rally that casts a Tavern spell): runs
    /// <paramref name="resolve"/>, then "after you cast a spell" hooks.
    /// </summary>
    public void CastSpell(Side caster, string spellCardId, Unit? target, Action<Fight>? resolve = null)
    {
        if (Tracing) Trace($"{(caster.IsOpponent ? "they" : "we")} cast {spellCardId}");
        resolve?.Invoke(this);
        Raise(caster, Hook.SpellCast, target, text: spellCardId);
    }

    // ================================================================================================ queries

    /// <summary>Living minions of a side (left to right), optionally of a tribe and without one minion.</summary>
    public List<Unit> Alive(Side side, Tribe tribe = Tribe.All, Unit? except = null)
    {
        var result = new List<Unit>(side.Count);
        foreach (var unit in side.Minions)
        {
            if (unit.IsAlive && unit != except && unit.Is(tribe))
            {
                result.Add(unit);
            }
        }

        return result;
    }

    public Unit? RandomOf(IReadOnlyList<Unit> candidates) => candidates.Count == 0 ? null : candidates[Random.Next(candidates.Count)];

    /// <summary>A random living friendly minion of <paramref name="tribe"/>, by default not <paramref name="self"/>.</summary>
    public Unit? RandomFriendly(Unit self, Tribe tribe = Tribe.All, bool includeSelf = false) =>
        RandomOf(Alive(self.Side, tribe, includeSelf ? null : self));

    public Unit? RandomEnemy(Unit self, Tribe tribe = Tribe.All) => RandomOf(Alive(self.Enemy, tribe));

    /// <summary>Up to <paramref name="count"/> different random picks, returned in board order.</summary>
    public List<Unit> RandomDistinct(IReadOnlyList<Unit> candidates, int count)
    {
        var pool = candidates.ToList();
        var picked = new List<Unit>(Math.Min(count, pool.Count));
        while (picked.Count < count && pool.Count > 0)
        {
            var index = Random.Next(pool.Count);
            picked.Add(pool[index]);
            pool.RemoveAt(index);
        }

        picked.Sort((a, b) => a.Position.CompareTo(b.Position));
        return picked;
    }

    /// <summary>The candidate with the highest <paramref name="key"/>; ties are broken at random.</summary>
    public Unit? Best(IReadOnlyList<Unit> candidates, Func<Unit, int> key)
    {
        Unit? best = null;
        int bestKey = int.MinValue, ties = 0;
        foreach (var unit in candidates)
        {
            var value = key(unit);
            if (best is null || value > bestKey)
            {
                best = unit;
                bestKey = value;
                ties = 1;
            }
            else if (value == bestKey && Random.Next(++ties) == 0)
            {
                best = unit;
            }
        }

        return best;
    }

    /// <summary>The minions next to <paramref name="unit"/> on its board (dying ones included), or null at the edges.</summary>
    public (Unit? Left, Unit? Right) Neighbours(Unit unit)
    {
        var index = unit.Position;
        if (index < 0)
        {
            return (null, null);
        }

        var board = unit.Side.Minions;
        return (index > 0 ? board[index - 1] : null, index + 1 < board.Count ? board[index + 1] : null);
    }

    // ================================================================================================ dispatch

    private void Raise(Side side, Hook hook, Unit? a, Unit? b = null, int amount = 0, AttackOutcome outcome = default,
                       string? text = null, Unit? exclude = null)
    {
        if ((side.Hooks & hook) == 0 || _depth >= MaxDepth)
        {
            return;
        }

        _depth++;
        try
        {
            var observers = Snapshot(side, out var count);
            for (var i = 0; i < count; i++)
            {
                var observer = observers[i];
                if (observer == exclude || (observer.Hooks & hook) == 0 || (!observer.IsOnBoard && !observer.IsExtra))
                {
                    continue;
                }

                foreach (var effect in observer.Effects)
                {
                    if ((effect.Hooks & hook) == 0)
                    {
                        continue;
                    }

                    switch (hook)
                    {
                        case Hook.FriendlyAttack: effect.OnFriendlyAttack(this, observer, a!, b!); break;
                        case Hook.FriendlyAttacked: effect.OnFriendlyAttacked(this, observer, a!, b!); break;
                        case Hook.AfterFriendlyAttack: effect.AfterFriendlyAttack(this, observer, a!, b!, outcome); break;
                        case Hook.FriendlyDealtDamage: effect.AfterFriendlyDealtDamage(this, observer, a!, b!, amount); break;
                        case Hook.ShieldLost: effect.OnFriendlyShieldLost(this, observer, a!); break;
                        case Hook.FriendlyDeathrattle: effect.OnFriendlyDeathrattle(this, observer, a!); break;
                        case Hook.FriendlyDeath: effect.AfterFriendlyDeath(this, observer, a!); break;
                        case Hook.EnemyDeath: effect.AfterEnemyDeath(this, observer, a!); break;
                        case Hook.WheneverSummon: effect.WheneverFriendlySummoned(this, observer, a!); break;
                        case Hook.AfterSummon: effect.AfterFriendlySummoned(this, observer, a!); break;
                        case Hook.Reborn: effect.AfterFriendlyReborn(this, observer, a!); break;
                        case Hook.SpellCast: effect.AfterFriendlySpellCast(this, observer, text ?? string.Empty, a); break;
                    }
                }
            }
        }
        finally
        {
            _depth--;
        }
    }

    private static string Name(Unit unit) => $"{(unit.Side.IsOpponent ? "their" : "our")} {unit}";
}
