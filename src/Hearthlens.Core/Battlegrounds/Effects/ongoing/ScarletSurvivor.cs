namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Scarlet Survivor (BG35_814): "Once this reaches 6 Attack, gain Divine Shield." Golden: same text. It fires once, when
/// the Attack goes from below 6 to 6 or more (any source: buffs, auras, summon buffs). A Survivor that already has 6
/// Attack when it enters the fight (captured, or a golden 6/6 or a reborn golden copy) counts as done: the card gives no
/// way to tell it from one that has already triggered, and it did not "reach" 6 in this combat. The flag is
/// <see cref="Unit.Counter2"/> (1 = done).
/// </summary>
[CardEffect("BG35_814")]
internal sealed class ScarletSurvivor : CombatEffect
{
    private const int Threshold = 6;

    public override void OnFightStart(Fight fight, Unit self) => self.Counter2 = self.Attack >= Threshold ? 1 : 0;

    public override void OnSummoned(Fight fight, Unit self) => self.Counter2 = self.Attack >= Threshold ? 1 : 0;

    public override void Refresh(Fight fight, Unit self)
    {
        if (self.Counter2 != 0 || !self.IsOnBoard || self.Attack < Threshold)
        {
            return;
        }

        self.Counter2 = 1;
        if (fight.Tracing) fight.Trace($"{self} reaches {Threshold} Attack and gains Divine Shield");
        fight.GiveKeywords(self, Keyword.DivineShield);
    }
}
