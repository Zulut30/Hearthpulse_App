namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Sin'dorei Straight Shot (BG25_016): "Divine Shield, Windfury. Rally: Remove Reborn and Taunt from the target."
/// Golden: same text (only the stats double). Divine Shield and Windfury are captured keywords; the keywords are removed
/// before damage, so a target killed by this attack does not come back.
/// </summary>
[CardEffect("BG25_016")]
internal sealed class SindoreiStraightShot : CombatEffect
{
    public override void OnAttack(Fight fight, Unit self, Unit target) => fight.RemoveKeywords(target, Keyword.Reborn | Keyword.Taunt);
}
