using Hearthlens.Core.Battlegrounds.Effects;

namespace Hearthlens.Core.Battlegrounds;

/// <summary>A minion during one simulated fight (mutable). Effects change it through <see cref="Fight"/> helpers where hooks must fire.</summary>
public sealed class Unit
{
    private CombatEffect[] _effects;

    internal Unit(SimMinion spec, Side side, CombatEffect[] effects, bool extra = false)
    {
        Spec = spec;
        Side = side;
        _effects = effects;
        IsExtra = extra;
        foreach (var effect in effects)
        {
            Hooks |= effect.Hooks;
        }

        Tribes = spec.Tribes;
        Attack = spec.Attack;
        Health = spec.Health;
        MaxHealth = Math.Max(spec.MaxHealth, spec.Health);
        Taunt = spec.Taunt;
        DivineShield = spec.DivineShield;
        Poisonous = spec.Poisonous;
        Venomous = spec.Venomous;
        Reborn = spec.Reborn;
        AttacksPerTurn = Math.Max(1, spec.AttacksPerTurn);
        Stealth = spec.Stealth;
        Counter = spec.ScriptData1;
        Counter2 = spec.ScriptData2;
    }

    /// <summary>What this unit was created from: the captured minion, a token, or a reborn copy.</summary>
    public SimMinion Spec { get; }

    public string CardId => Spec.CardId;

    public bool Golden => Spec.Golden;

    public int EntityId => Spec.EntityId;

    public CardMechanic Mechanics => Spec.Mechanics;

    public Side Side { get; }

    public Side Enemy => Side.Enemy;

    public Tribe Tribes { get; set; }

    public int Attack { get; set; }

    public int Health { get; set; }

    public int MaxHealth { get; set; }

    public bool Taunt { get; set; }

    public bool DivineShield { get; set; }

    public bool Poisonous { get; set; }

    public bool Venomous { get; set; }

    public bool Reborn { get; set; }

    public bool Stealth { get; set; }

    public int AttacksPerTurn { get; set; }

    /// <summary>Free per-fight state for effects. Starts at the captured TAG_SCRIPT_DATA_NUM_1 (0 for summoned units).</summary>
    public int Counter { get; set; }

    /// <summary>Free per-fight state for effects. Starts at the captured TAG_SCRIPT_DATA_NUM_2.</summary>
    public int Counter2 { get; set; }

    /// <summary>Friendly deaths counted towards Avenge since it last fired (see <see cref="AvengeEffect"/>).</summary>
    public int AvengeCount { get; set; }

    /// <summary>On its side's board (dying minions stay on the board until the death phase removes them).</summary>
    public bool IsOnBoard { get; internal set; }

    /// <summary>A player-level effect holder (hero power, trinket, Deity...), never on the board.</summary>
    public bool IsExtra { get; }

    /// <summary>Destroyed by an effect; removed at the next death phase.</summary>
    public bool MarkedForDeath { get; internal set; }

    /// <summary>Mortally wounded or destroyed, waiting for the death phase.</summary>
    public bool IsDying => Health <= 0 || MarkedForDeath;

    /// <summary>On the board and not dying: what random picks, "your minions" and targeting consider.</summary>
    public bool IsAlive => IsOnBoard && !IsDying;

    /// <summary>The minion whose damage or destroy effect killed this one (null for other causes).</summary>
    public Unit? Killer { get; internal set; }

    /// <summary>For a reborn copy: the minion it came back from.</summary>
    public Unit? RebornFrom { get; internal set; }

    /// <summary>Index on its side's board, or -1 when off the board.</summary>
    public int Position => IsOnBoard ? Side.IndexOf(this) : -1;

    public Keyword Keywords =>
        (Taunt ? Keyword.Taunt : 0) | (DivineShield ? Keyword.DivineShield : 0) | (Poisonous ? Keyword.Poisonous : 0)
        | (Venomous ? Keyword.Venomous : 0) | (Reborn ? Keyword.Reborn : 0) | (Stealth ? Keyword.Stealth : 0)
        | (AttacksPerTurn >= 4 ? Keyword.MegaWindfury : AttacksPerTurn >= 2 ? Keyword.Windfury : 0);

    /// <summary>Its card effect first, then enchantment effects and effects gained in combat.</summary>
    public IReadOnlyList<CombatEffect> Effects => _effects;

    /// <summary>Has a deathrattle: printed on the card, or from a registered effect.</summary>
    public bool HasDeathrattle => (Spec.Mechanics & CardMechanic.Deathrattle) != 0 || (Hooks & Hook.Deathrattle) != 0;

    public bool IsRally => (Spec.Mechanics & CardMechanic.Rally) != 0;

    public bool Is(Tribe tribe) => tribe == Tribe.All || (Tribes & tribe) != 0;

    /// <summary><paramref name="golden"/> for a golden minion, else <paramref name="normal"/> (read the golden card's own text).</summary>
    public int Pick(int normal, int golden) => Golden ? golden : normal;

    internal Hook Hooks { get; private set; }

    // Death bookkeeping, set when the death phase removes the unit.
    internal Unit? DeathAnchor { get; set; }

    internal bool DiedLeftOfGap { get; set; }

    internal int DeathIndex { get; set; }

    // Aura stats currently applied to this unit (captured stats already include the auras present at capture).
    internal StatDelta AppliedAura { get; set; }

    /// <summary>Adds an effect for the rest of the fight (e.g. a gained deathrattle). Lost on Reborn.</summary>
    public void AddEffect(CombatEffect effect)
    {
        var effects = new CombatEffect[_effects.Length + 1];
        _effects.CopyTo(effects, 0);
        effects[^1] = effect;
        _effects = effects;
        Hooks |= effect.Hooks;
        Side.InvalidateHooks();
    }

    /// <summary>Current state as a <see cref="SimMinion"/> (for results and tests).</summary>
    public SimMinion ToSimMinion() =>
        Spec with
        {
            Attack = Attack,
            Health = Health,
            Taunt = Taunt,
            DivineShield = DivineShield,
            Poisonous = Poisonous,
            Venomous = Venomous,
            Reborn = Reborn,
            AttacksPerTurn = AttacksPerTurn,
            Stealth = Stealth,
            MaxHealth = MaxHealth,
            Tribes = Tribes,
        };

    public override string ToString() =>
        $"{(CardId.Length > 0 ? CardId : "minion")}{(Golden ? "(G)" : string.Empty)} {Attack}/{Health}{(Keywords != Keyword.None ? " " + Keywords : string.Empty)}";
}
