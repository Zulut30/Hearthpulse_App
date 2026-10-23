namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// "Has +X/+Y for each &lt;event&gt; this game (wherever this is)": Eternal Knight, San'layn Scribe, Falling Sky Golem.
/// <para>
/// A captured minion's stats already include the events before the fight. During the fight it catches up with the
/// events of this fight (<see cref="EventsThisFight"/>); <see cref="Unit.Counter2"/> holds how many of them its stats
/// already include, so several hooks for one event never double count. A minion summoned in combat (token, reborn
/// copy) starts from its printed stats plus X/Y × (events before the fight + events so far this fight).
/// </para>
/// <para>
/// Events before the fight are not in the log as a number: they are inferred from the captured copies of the same card
/// on the same board, as the smallest ((Attack − printed Attack) / X, (max Health − printed Health) / Y). Extra buffs
/// on every copy make this an overestimate; without a captured copy it is 0.
/// </para>
/// </summary>
internal abstract class CountingAura : CombatEffect
{
    /// <summary>Card id whose captured copies tell how many events happened before the fight.</summary>
    protected abstract string CardId { get; }

    /// <summary>Printed stats of the normal card (golden: doubled).</summary>
    protected abstract (int Attack, int Health) Printed { get; }

    /// <summary>Printed bonus per event of the normal card (golden: doubled).</summary>
    protected abstract (int Attack, int Health) PerEvent { get; }

    /// <summary>Events of this fight so far on <paramref name="side"/>.</summary>
    protected abstract int EventsThisFight(Fight fight, Side side);

    public override void OnFightStart(Fight fight, Unit self) => self.Counter2 = EventsThisFight(fight, self.Side);

    public override void OnSummoned(Fight fight, Unit self)
    {
        var now = EventsThisFight(fight, self.Side);
        var total = EventsBeforeFight(self.Side) + now;
        self.Counter2 = now;
        if (total > 0)
        {
            var (attack, health) = Bonus(self.Spec);
            fight.Buff(self, attack * total, health * total);
        }
    }

    /// <summary>Applies the events of this fight that this minion's stats do not include yet.</summary>
    protected void CatchUp(Fight fight, Unit self)
    {
        if (!self.IsAlive)
        {
            return;
        }

        var now = EventsThisFight(fight, self.Side);
        if (now <= self.Counter2)
        {
            return;
        }

        var (attack, health) = Bonus(self.Spec);
        var missing = now - self.Counter2;
        self.Counter2 = now;
        fight.Buff(self, attack * missing, health * missing);
    }

    /// <summary>Bonus per event: the captured script data when present (TAG_SCRIPT_DATA_NUM_1/2), else the printed numbers.</summary>
    private (int Attack, int Health) Bonus(SimMinion spec)
    {
        var factor = spec.Golden ? 2 : 1;
        return (spec.ScriptData1 > 0 ? spec.ScriptData1 : PerEvent.Attack * factor,
                spec.ScriptData2 > 0 ? spec.ScriptData2 : PerEvent.Health * factor);
    }

    /// <summary>Events before this fight, inferred from the captured copies of the card on this side.</summary>
    internal int EventsBeforeFight(Side side)
    {
        int? fewest = null;
        foreach (var minion in side.Info.Minions)
        {
            if (minion.CardId != CardId)
            {
                continue;
            }

            var factor = minion.Golden ? 2 : 1;
            var (perAttack, perHealth) = Bonus(minion);
            var printedAttack = minion.RebornAttack > 0 ? minion.RebornAttack : Printed.Attack * factor;
            var printedHealth = minion.BaseHealth > 0 ? minion.BaseHealth : Printed.Health * factor;
            var maxHealth = Math.Max(minion.MaxHealth, minion.Health);
            var byAttack = perAttack > 0 ? (minion.Attack - printedAttack) / perAttack : int.MaxValue;
            var byHealth = perHealth > 0 ? (maxHealth - printedHealth) / perHealth : int.MaxValue;
            var events = Math.Max(0, Math.Min(byAttack, byHealth));
            fewest = fewest is null ? events : Math.Min(fewest.Value, events);
        }

        return fewest ?? 0;
    }
}
