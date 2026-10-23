using Hearthlens.Core.Battlegrounds.Effects;

namespace Hearthlens.Core.Battlegrounds;

/// <summary>One player's side during a simulated fight: board, player-level effects and per-fight counters.</summary>
public sealed class Side
{
    public const int MaxMinions = 7;

    private readonly List<Unit> _board = new(MaxMinions);
    private readonly List<Unit> _extras = new();
    private readonly List<Unit> _dead = new();
    private Dictionary<string, int>? _counters;
    private List<(Func<Unit, bool> Applies, int Attack, int Health)>? _summonBuffs;
    private List<SimMinion>? _hand;
    private Hook _hooks;
    private bool _hooksDirty = true;

    internal Side(Fight fight, int index, SimSide info)
    {
        Fight = fight;
        Index = index;
        Info = info;
    }

    public Fight Fight { get; }

    /// <summary>0 = the local player ("ours"), 1 = the opponent.</summary>
    public int Index { get; }

    public bool IsOpponent => Index == 1;

    /// <summary>What was captured for this player (hero, hero power, tags, the local player's hand).</summary>
    public SimSide Info { get; }

    public Side Enemy { get; internal set; } = null!;

    /// <summary>Minions on the board, left to right (dying ones included until the death phase).</summary>
    public IReadOnlyList<Unit> Minions => _board;

    public int Count => _board.Count;

    public bool IsFull => _board.Count >= MaxMinions;

    /// <summary>Player-level effect holders (hero power, trinkets, Deity...), never on the board.</summary>
    public IReadOnlyList<Unit> Extras => _extras;

    /// <summary>Minions that died this fight, in the order they died.</summary>
    public IReadOnlyList<Unit> DeadThisFight => _dead;

    /// <summary>Deathrattle triggers resolved this fight (multipliers included).</summary>
    public int DeathrattlesTriggered { get; internal set; }

    /// <summary>Extra deathrattle triggers from player-level sources (quest rewards, anomaly...), added to minion multipliers.</summary>
    public int ExtraDeathrattles { get; set; }

    /// <summary>
    /// The local player's hand (a copy effects may change); always empty for the opponent, whose hand is hidden.
    /// </summary>
    public List<SimMinion> Hand => _hand ??= IsOpponent ? new List<SimMinion>() : Info.Hand.ToList();

    /// <summary>
    /// Attack order: the next attacker is the first minion at or right of this index (wrapping). It sits just right of the
    /// last attacker, so minions summoned there attack before those further right, and minions summoned to its left wait
    /// for the next round.
    /// </summary>
    internal int Gap { get; set; }

    internal Hook Hooks
    {
        get
        {
            if (_hooksDirty)
            {
                _hooks = Hook.None;
                foreach (var unit in _board) _hooks |= unit.Hooks;
                foreach (var unit in _extras) _hooks |= unit.Hooks;
                _hooksDirty = false;
            }

            return _hooks;
        }
    }

    internal bool HasAppliedAuras { get; set; }

    /// <summary>Minions entered or left since auras were last recomputed.</summary>
    internal bool AuraDirty { get; set; } = true;

    public int IndexOf(Unit unit) => _board.IndexOf(unit);

    /// <summary>
    /// A per-fight player counter (e.g. "EternalKnightsDead", "BeetleAttack"). Before it is set it reads the captured
    /// player tag of the same name, else 0.
    /// </summary>
    public int Counter(string key) =>
        _counters is not null && _counters.TryGetValue(key, out var value) ? value : Info.Tags.GetValueOrDefault(key);

    public void SetCounter(string key, int value) => (_counters ??= new Dictionary<string, int>(StringComparer.Ordinal))[key] = value;

    public int AddCounter(string key, int delta)
    {
        var value = Counter(key) + delta;
        SetCounter(key, value);
        return value;
    }

    /// <summary>Stats every minion summoned later this fight gets if <paramref name="applies"/> (e.g. "your Beasts have +7/+7").</summary>
    public void AddSummonBuff(Func<Unit, bool> applies, int attack, int health) =>
        (_summonBuffs ??= new()).Add((applies, attack, health));

    /// <summary>Stats every minion of <paramref name="tribe"/> (<see cref="Tribe.All"/>: every minion) summoned later this fight gets.</summary>
    public void AddSummonBuff(Tribe tribe, int attack, int health) => AddSummonBuff(u => u.Is(tribe), attack, health);

    internal void ApplySummonBuffs(Unit unit)
    {
        if (_summonBuffs is null)
        {
            return;
        }

        foreach (var (applies, attack, health) in _summonBuffs)
        {
            if (applies(unit))
            {
                unit.Attack = Math.Max(0, unit.Attack + attack);
                unit.Health += health;
                unit.MaxHealth += health;
            }
        }
    }

    internal void AddInitial(Unit unit)
    {
        _board.Add(unit);
        unit.IsOnBoard = true;
        _hooksDirty = true;
        AuraDirty = true;
    }

    internal void AddExtra(Unit unit)
    {
        _extras.Add(unit);
        _hooksDirty = true;
    }

    internal void Insert(int index, Unit unit, bool leftOfGap)
    {
        index = Math.Clamp(index, 0, _board.Count);
        _board.Insert(index, unit);
        unit.IsOnBoard = true;
        if (index < Gap || (index == Gap && leftOfGap))
        {
            Gap++;
        }

        _hooksDirty = true;
        AuraDirty = true;
    }

    internal void Remove(Unit unit)
    {
        var index = _board.IndexOf(unit);
        if (index < 0)
        {
            return;
        }

        _board.RemoveAt(index);
        unit.IsOnBoard = false;
        if (index < Gap)
        {
            Gap--;
        }

        _dead.Add(unit);
        _hooksDirty = true;
        AuraDirty = true;
    }

    internal void InvalidateHooks()
    {
        _hooksDirty = true;
        AuraDirty = true;
    }

    /// <summary>The minion that attacks next: the first one with Attack at or right of the gap, wrapping around.</summary>
    internal Unit? NextAttacker()
    {
        var count = _board.Count;
        if (count == 0)
        {
            return null;
        }

        var start = Gap >= count ? 0 : Gap;
        for (var i = 0; i < count; i++)
        {
            var unit = _board[(start + i) % count];
            if (unit.Attack > 0 && !unit.IsDying)
            {
                return unit;
            }
        }

        return null;
    }

    /// <summary>Copies board and extra units (so hooks can change the board while being dispatched).</summary>
    internal void CopyObservers(Unit[] buffer)
    {
        _board.CopyTo(buffer);
        _extras.CopyTo(buffer, _board.Count);
    }

    public override string ToString() => string.Join(", ", _board);
}
