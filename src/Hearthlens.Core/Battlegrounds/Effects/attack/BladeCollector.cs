namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Blade Collector (BG26_817): "Also damages the enemies next to whomever this attacks." Golden: same text.
/// </summary>
/// <remarks>
/// Cleave: during its combat damage, the target's neighbours take the same damage (this minion's Attack) at the same
/// time; they do not hit back. Poisonous applies to them too; Venomous is used up by the main hit (engine rule).
/// </remarks>
[CardEffect("BG26_817")]
internal sealed class BladeCollector : CombatEffect
{
    public override void OnAttackDamage(Fight fight, Unit self, Unit target)
    {
        var (left, right) = fight.Neighbours(target);
        if (left is not null) fight.DealDamage(self, left, self.Attack);
        if (right is not null) fight.DealDamage(self, right, self.Attack);
    }
}
