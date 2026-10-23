namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>Wolf Pup (BG36_207): "Rally: Give your other minions +4/+1." Golden: "+8/+2."</summary>
[CardEffect("BG36_207")]
internal sealed class WolfPup : CombatEffect
{
    public override void OnAttack(Fight fight, Unit self, Unit target)
    {
        foreach (var unit in fight.Alive(self.Side, except: self))
        {
            fight.Buff(unit, self.Pick(4, 8), self.Pick(1, 2));
        }
    }
}
