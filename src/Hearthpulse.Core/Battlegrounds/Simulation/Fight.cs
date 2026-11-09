using Hearthpulse.Core.Battlegrounds.Effects;

namespace Hearthpulse.Core.Battlegrounds;

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
public sealed partial class Fight
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
}
