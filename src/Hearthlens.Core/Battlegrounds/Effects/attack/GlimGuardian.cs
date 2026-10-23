namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>Glim Guardian (BG29_888): "Rally: Gain +2 Attack." Golden: "Gain +4 Attack."</summary>
[CardEffect("BG29_888")]
internal sealed class GlimGuardian : CombatEffect
{
    public override void OnAttack(Fight fight, Unit self, Unit target) => fight.Buff(self, self.Pick(2, 4), 0);
}
