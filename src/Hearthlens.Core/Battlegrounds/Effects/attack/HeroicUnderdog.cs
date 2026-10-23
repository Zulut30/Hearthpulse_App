namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Heroic Underdog (BG34_604): "Stealth. Rally: Gain the target's Attack." Golden: "Gain double the target's Attack."
/// The target's Attack is read when the Rally triggers, before damage. Stealth is a captured keyword.
/// </summary>
[CardEffect("BG34_604")]
internal sealed class HeroicUnderdog : CombatEffect
{
    public override void OnAttack(Fight fight, Unit self, Unit target) => fight.Buff(self, target.Attack * self.Pick(1, 2), 0);
}
