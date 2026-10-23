namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Razorfen Vineweaver (BG33_883): "Rally: This plays 4 permanent Blood Gems on itself." Golden: "8 permanent Blood Gems".
/// "Permanent" only matters after combat.
/// </summary>
[CardEffect("BG33_883")]
internal sealed class RazorfenVineweaver : CombatEffect
{
    public override void OnAttack(Fight fight, Unit self, Unit target) => BloodGems.Play(fight, self, self, self.Pick(4, 8));
}
