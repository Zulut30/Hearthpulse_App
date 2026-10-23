namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Heroic Broodmother (BG36_849): "Rally: Gain Divine Shield. Start of Combat: Attack immediately."
/// Golden: "... Start of Combat: Attack immediately twice." Only the Rally is simulated here: the start-of-combat attack
/// (and the Rally it triggered) happened before the boards were captured, so it is already in the captured state.
/// </summary>
[CardEffect("BG36_849")]
internal sealed class HeroicBroodmother : CombatEffect
{
    public override void OnAttack(Fight fight, Unit self, Unit target) => fight.GiveKeywords(self, Keyword.DivineShield);
}
