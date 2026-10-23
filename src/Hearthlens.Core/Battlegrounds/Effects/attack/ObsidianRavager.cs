namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Obsidian Ravager (BG27_017): "Rally: Deal damage equal to this minion's Attack to the target and an adjacent minion."
/// Golden: "Rally: Deal damage equal to this minion's Attack to the target and its neighbors."
/// </summary>
/// <remarks>
/// Before damage. The adjacent minion (normal card: one living neighbour of the target at random; golden: each living
/// neighbour) is chosen before any damage is dealt; the target is hit first, then the neighbours left to right. If this
/// kills the target, the attack itself is cancelled (engine rule), so the Ravager takes no damage from it.
/// </remarks>
[CardEffect("BG27_017")]
internal sealed class ObsidianRavager : CombatEffect
{
    public override void OnAttack(Fight fight, Unit self, Unit target)
    {
        var damage = self.Attack;
        if (damage <= 0)
        {
            return;
        }

        var (left, right) = fight.Neighbours(target);
        var adjacent = new List<Unit>(2);
        if (left is { IsAlive: true }) adjacent.Add(left);
        if (right is { IsAlive: true }) adjacent.Add(right);
        if (!self.Golden && adjacent.Count > 1)
        {
            adjacent = [fight.RandomOf(adjacent)!];
        }

        fight.DealDamage(self, target, damage);
        foreach (var unit in adjacent)
        {
            fight.DealDamage(self, unit, damage);
        }
    }
}
