namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Faceless Converter (BG36_318): "Deathrattle: Give your Deity +{0}/+{1}. (Improved by each Tavern spell you've cast this
/// game!)" Printed +2/+1, golden +4/+2. The improved values are the captured TAG_SCRIPT_DATA_NUM_1/2 (the card's text
/// variables); without them the printed numbers are used. See <see cref="Deity.Give"/>.
/// </summary>
[CardEffect("BG36_318")]
internal sealed class FacelessConverter : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
        var attack = self.Spec.ScriptData1 > 0 ? self.Spec.ScriptData1 : self.Pick(2, 4);
        var health = self.Spec.ScriptData2 > 0 ? self.Spec.ScriptData2 : self.Pick(1, 2);
        Deity.Give(fight, self.Side, attack, health);
    }
}
