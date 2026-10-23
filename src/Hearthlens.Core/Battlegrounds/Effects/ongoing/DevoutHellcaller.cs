namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Devout Hellcaller (BG33_155): "After another friendly Demon deals damage, gain +2/+2 permanently." Golden: "+4/+4".
/// Counts combat and effect damage that got through (a hit on Divine Shield deals no damage), once per damage event.
/// A mortally wounded Hellcaller does not grow.
/// </summary>
[CardEffect("BG33_155")]
internal sealed class DevoutHellcaller : CombatEffect
{
    public override void AfterFriendlyDealtDamage(Fight fight, Unit self, Unit dealer, Unit victim, int amount)
    {
        if (dealer == self || amount <= 0 || !dealer.Is(Tribe.Demon) || !self.IsAlive)
        {
            return;
        }

        fight.Buff(self, self.Pick(2, 4), self.Pick(2, 4));
    }
}
