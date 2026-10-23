namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Bonker (BG20_104): "Windfury. Rally: This plays a Blood Gem on all your other minions." Golden: "plays 2 Blood Gems".
/// Windfury is a captured keyword; the Rally runs on every attack, so on both Windfury swings.
/// </summary>
[CardEffect("BG20_104")]
internal sealed class Bonker : CombatEffect
{
    public override void OnAttack(Fight fight, Unit self, Unit target)
    {
        foreach (var unit in fight.Alive(self.Side, except: self))
        {
            BloodGems.Play(fight, self, unit, self.Pick(1, 2));
        }
    }
}
