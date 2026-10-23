namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>Cadaver Caretaker (BG30_125): "Deathrattle: Summon three 1/1 Skeletons." Golden: six 1/1 Skeletons.</summary>
[CardEffect("BG30_125")]
internal sealed class CadaverCaretaker : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) => fight.SummonAt(self, Tokens.Skeleton(), self.Pick(3, 6));
}
