using Hearthpulse.Core.Battlegrounds.Effects;

namespace Hearthpulse.Core.Battlegrounds;

/// <summary>Changing units: buffs, set stats, keywords, auras and spells cast in combat.</summary>
public sealed partial class Fight
{
    /// <summary>Gives +Attack/+Health (negative values reduce; Attack never goes below 0).</summary>
    public void Buff(Unit target, int attack, int health)
    {
        target.Attack = (int)Math.Clamp((long)target.Attack + attack, 0, int.MaxValue);
        target.Health = (int)Math.Clamp((long)target.Health + health, -1_000_000_000, int.MaxValue);
        target.MaxHealth = (int)Math.Clamp((long)target.MaxHealth + health, 0, int.MaxValue);
        RefreshUnit(target);
    }

    /// <summary>Sets Attack and Health (and maximum Health).</summary>
    public void SetStats(Unit target, int attack, int health)
    {
        target.Attack = Math.Max(0, attack);
        target.Health = health;
        target.MaxHealth = Math.Max(0, health);
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
                    // Summed in 64 bits and clamped, like Buff: a huge aura on a huge minion must not wrap around.
                    unit.Attack = (int)Math.Clamp((long)unit.Attack + delta.Attack, 0, int.MaxValue);
                    unit.MaxHealth = (int)Math.Clamp((long)unit.MaxHealth + delta.Health, 0, int.MaxValue);
                    // A removed Health aura lowers current Health only down to the new maximum.
                    unit.Health = delta.Health > 0
                        ? (int)Math.Clamp((long)unit.Health + delta.Health, -1_000_000_000, int.MaxValue)
                        : Math.Min(unit.Health, unit.MaxHealth);
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
}
