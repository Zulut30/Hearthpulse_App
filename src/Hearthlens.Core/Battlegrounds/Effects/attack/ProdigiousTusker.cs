namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Prodigious Tusker (BG33_430): "Whenever another friendly minion attacks, this plays a Blood Gem on it."
/// Golden: "plays 2 Blood Gems on it." Before damage, after the attacker's own Rally.
/// </summary>
[CardEffect("BG33_430")]
internal sealed class ProdigiousTusker : CombatEffect
{
    public override void OnFriendlyAttack(Fight fight, Unit self, Unit attacker, Unit target)
    {
        if (attacker != self && attacker.IsOnBoard)
        {
            BloodGems.Play(fight, self, attacker, self.Pick(1, 2));
        }
    }
}
