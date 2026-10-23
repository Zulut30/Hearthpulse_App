namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Roaring Recruiter (BG29_816): "Whenever another friendly Dragon attacks, give it +3/+1." Golden: "+6/+2."
/// Before damage, after the attacker's own Rally.
/// </summary>
[CardEffect("BG29_816")]
internal sealed class RoaringRecruiter : CombatEffect
{
    public override void OnFriendlyAttack(Fight fight, Unit self, Unit attacker, Unit target)
    {
        if (attacker != self && attacker.IsOnBoard && attacker.Is(Tribe.Dragon))
        {
            fight.Buff(attacker, self.Pick(3, 6), self.Pick(1, 2));
        }
    }
}
