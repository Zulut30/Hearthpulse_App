namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>Harmless Bonehead (BG28_300): "Deathrattle: Summon two 1/1 Skeletons." Golden: four 1/1 Skeletons.</summary>
[CardEffect("BG28_300")]
internal sealed class HarmlessBonehead : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) => fight.SummonAt(self, Tokens.Skeleton(), self.Pick(2, 4));
}
