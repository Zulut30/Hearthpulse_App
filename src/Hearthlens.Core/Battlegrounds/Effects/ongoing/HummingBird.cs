namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Humming Bird (BG26_805): "Start of Combat: For the rest of this combat, your Beasts have +1 Attack." Golden: "+2
/// Attack." The Start of Combat part resolved before capture, so the Beasts on the board already have it. What is left
/// is the "rest of this combat" part: Beasts summoned later (tokens, reborn copies) get +1 Attack too, even after the
/// Bird dies.
/// </summary>
[CardEffect("BG26_805")]
internal sealed class HummingBird : CombatEffect
{
    public override void OnFightStart(Fight fight, Unit self) => self.Side.AddSummonBuff(Tribe.Beast, self.Pick(1, 2), 0);
}
