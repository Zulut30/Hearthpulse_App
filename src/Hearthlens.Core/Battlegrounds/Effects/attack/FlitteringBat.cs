namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Flittering Bat (BG36_200): "Rally: Summon a 1/1 Beast." Golden: "Rally: Summon two 1/1 Beasts."
/// The Beast is the Foraging Bat token (BG36_200t, 1/1); the golden card summons two normal 1/1s, not golden ones.
/// They appear to the right of the bat, before damage.
/// </summary>
[CardEffect("BG36_200")]
internal sealed class FlitteringBat : CombatEffect
{
    public override void OnAttack(Fight fight, Unit self, Unit target) => fight.SummonAt(self, Tokens.ForagingBat(), self.Pick(1, 2));
}
