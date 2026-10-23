namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// De-volition-ist (BG36_102): "After this attacks, deal damage equal to this minion's Attack to the highest-Health enemy
/// minion." Golden: "... deal damage equal to double this minion's Attack ...".
/// </summary>
/// <remarks>
/// Runs after the combat damage, before the death phase. Assumptions: "enemy minion" means a living one (a target this
/// attack just killed is skipped), Health is current Health, ties are broken at random, and the effect also fires when
/// this minion was mortally wounded by the exchange (it is still in play, as for other "after this attacks" triggers).
/// </remarks>
[CardEffect("BG36_102")]
internal sealed class DeVolitionist : CombatEffect
{
    public override void AfterAttack(Fight fight, Unit self, Unit target, AttackOutcome outcome)
    {
        var damage = self.Attack * self.Pick(1, 2);
        if (damage > 0 && fight.Best(fight.Alive(self.Enemy), u => u.Health) is { } victim)
        {
            fight.DealDamage(self, victim, damage);
        }
    }
}
