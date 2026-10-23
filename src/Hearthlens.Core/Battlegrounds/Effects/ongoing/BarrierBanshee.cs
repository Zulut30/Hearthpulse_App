namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Barrier Banshee (BG36_514): "After a friendly minion is Reborn, gain Divine Shield and +8/+8." Golden: "+16/+16".
/// Its own reborn copy does not see its own Reborn (engine rule: the new copy is excluded).
/// </summary>
[CardEffect("BG36_514")]
internal sealed class BarrierBanshee : CombatEffect
{
    public override void AfterFriendlyReborn(Fight fight, Unit self, Unit reborn)
    {
        if (!self.IsAlive)
        {
            return;
        }

        fight.GiveKeywords(self, Keyword.DivineShield);
        fight.Buff(self, self.Pick(8, 16), self.Pick(8, 16));
    }
}
