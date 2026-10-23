namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Eternal Tycoon (BG34_403): "Avenge (5): Summon an Eternal Knight. It attacks immediately." Golden: "Summon a Golden
/// Eternal Knight." The Knight is summoned to the Tycoon's right and attacks right after the attack in progress
/// (<see cref="Fight.AttackSoon"/>). Its "+4/+2 for each friendly Eternal Knight that died this game" comes from the
/// Eternal Knight's own effect (<see cref="EternalKnight"/>). On a full board nothing is summoned.
/// </summary>
[CardEffect("BG34_403")]
internal sealed class EternalTycoon : AvengeEffect
{
    protected override int Threshold(Unit self) => 5;

    protected override void Avenge(Fight fight, Unit self)
    {
        if (fight.SummonAt(self, Tokens.EternalKnight(self.Golden)) is { } knight)
        {
            fight.AttackSoon(knight);
        }
    }
}
