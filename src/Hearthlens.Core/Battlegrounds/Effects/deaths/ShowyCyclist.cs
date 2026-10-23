namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Showy Cyclist (BG31_925): "Deathrattle: Give all your Naga +{1}/+{3}. (Improved by every 3 spells you've cast this
/// game!)" (+2/+1; golden +4/+2.)
/// </summary>
/// <remarks>
/// The card text fills {1} from TAG_SCRIPT_DATA_NUM_2 (the Attack bonus) and {3} from TAG_SCRIPT_DATA_NUM_4 (the Health
/// bonus), which the capture does not keep. Assumption: each improvement adds the printed +2/+1 (golden +4/+2), so the
/// Health bonus is half the Attack bonus. Without script data the printed values are used. Spells cast during the fight
/// do not improve it (the improvement would apply to the next deathrattle only).
/// </remarks>
[CardEffect("BG31_925")]
internal sealed class ShowyCyclist : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
        var shown = self.Spec.ScriptData2;
        var attack = shown > 0 ? shown : self.Pick(2, 4);
        var health = shown > 0 ? Math.Max(1, shown / 2) : self.Pick(1, 2);
        DeathKit.BuffAll(fight, self.Side, Tribe.Naga, attack, health);
    }
}
