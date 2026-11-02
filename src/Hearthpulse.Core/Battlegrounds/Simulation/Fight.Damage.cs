using Hearthpulse.Core.Battlegrounds.Effects;

namespace Hearthpulse.Core.Battlegrounds;

/// <summary>Damage and its batches: shields, poison and venom, kills, and the damage events effects react to.</summary>
public sealed partial class Fight
{
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
        target.Health = (int)Math.Clamp((long)target.Health - amount, -1_000_000_000, int.MaxValue);
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
}
