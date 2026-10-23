namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Relentless Deflector (BG34_405): "Has Taunt while this has Divine Shield. Avenge (3): Gain Divine Shield." Golden: same
/// text. Taunt follows Divine Shield: it goes when the shield breaks and comes back with the Avenge shield. A Deflector
/// captured with Taunt but no Divine Shield got its Taunt from something else and keeps it. Avenge counts friendly
/// deaths from the start of the fight (engine rule).
/// </summary>
[CardEffect("BG34_405")]
internal sealed class RelentlessDeflector : AvengeEffect
{
    protected override int Threshold(Unit self) => 3;

    protected override void Avenge(Fight fight, Unit self)
    {
        if (fight.Tracing) fight.Trace($"avenge: {self} gains Divine Shield");
        fight.GiveKeywords(self, Keyword.DivineShield);
    }

    public override void Refresh(Fight fight, Unit self) => self.Taunt = self.DivineShield || HasOwnTaunt(self);

    public override void OnFriendlyShieldLost(Fight fight, Unit self, Unit unit)
    {
        if (unit == self)
        {
            Refresh(fight, self);
        }
    }

    /// <summary>Taunt from another source: captured with Taunt while it had no Divine Shield.</summary>
    private static bool HasOwnTaunt(Unit self) => self.Spec.Taunt && !self.Spec.DivineShield;
}
