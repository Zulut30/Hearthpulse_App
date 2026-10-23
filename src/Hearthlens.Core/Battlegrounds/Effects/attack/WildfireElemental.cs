namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Wildfire Elemental (BGS_126): "After this attacks and kills a minion, deal excess damage to an adjacent enemy."
/// Golden (TB_BaconUps_166): "... deal excess damage to both adjacent enemies."
/// </summary>
/// <remarks>
/// Runs after the combat damage, before the death phase, so the killed target is still on the board and "adjacent"
/// means its neighbours. Excess damage is the damage beyond the target's remaining Health (0 after Divine Shield, so
/// nothing happens). The normal card picks one of the living neighbours at random; the golden card hits each living
/// neighbour. It also fires when this minion dies in the same exchange (it is mortally wounded but still in play).
/// </remarks>
[CardEffect("BGS_126")]
internal sealed class WildfireElemental : CombatEffect
{
    public override void AfterAttack(Fight fight, Unit self, Unit target, AttackOutcome outcome)
    {
        if (!outcome.Killed || outcome.Excess <= 0)
        {
            return;
        }

        var (left, right) = fight.Neighbours(target);
        var adjacent = new List<Unit>(2);
        if (left is { IsAlive: true }) adjacent.Add(left);
        if (right is { IsAlive: true }) adjacent.Add(right);
        if (adjacent.Count == 0)
        {
            return;
        }

        if (self.Golden)
        {
            foreach (var unit in adjacent)
            {
                fight.DealDamage(self, unit, outcome.Excess);
            }
        }
        else if (fight.RandomOf(adjacent) is { } unit)
        {
            fight.DealDamage(self, unit, outcome.Excess);
        }
    }
}
