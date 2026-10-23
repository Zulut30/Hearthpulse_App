namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Drifting Sacrifice (BG36_113): "Reborn. Deathrattle: Give your Deity +2/+1." Golden: "+4/+2". Reborn is a keyword the
/// engine handles, so the deathrattle runs on each of its two lives. See <see cref="Deity.Give"/>.
/// </summary>
[CardEffect("BG36_113")]
internal sealed class DriftingSacrifice : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) =>
        Deity.Give(fight, self.Side, self.Pick(2, 4), self.Pick(1, 2));
}
