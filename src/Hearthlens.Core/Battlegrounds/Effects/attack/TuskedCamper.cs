namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>Tusked Camper (BG33_886): "Rally: This plays a Blood Gem on itself." Golden: "plays 2 Blood Gems on itself."</summary>
[CardEffect("BG33_886")]
internal sealed class TuskedCamper : CombatEffect
{
    public override void OnAttack(Fight fight, Unit self, Unit target) => BloodGems.Play(fight, self, self, self.Pick(1, 2));
}
