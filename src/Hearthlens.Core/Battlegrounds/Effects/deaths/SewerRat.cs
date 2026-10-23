namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Sewer Rat (BG19_010, token of Sewer Lord): "Deathrattle: Summon a 2/3 Turtle with Taunt." (Half-Shell). Golden Rat:
/// "Summon a 4/6 Turtle with Taunt." (golden Half-Shell).
/// </summary>
[CardEffect(Tokens.SewerRatId)]
internal sealed class SewerRat : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) => fight.SummonAt(self, Tokens.HalfShell(self.Golden));
}
