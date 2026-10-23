namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Sewer Lord (BG35_604): "Deathrattle: Summon two Sewer Rats that summon 2/3 Turtles with Taunt." Golden: "Summon two
/// Golden Sewer Rats that summon 4/6 Turtles with Taunt." The Rats' own deathrattle is <see cref="SewerRat"/>.
/// </summary>
[CardEffect("BG35_604")]
internal sealed class SewerLord : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) => fight.SummonAt(self, Tokens.SewerRat(self.Golden), 2);
}
