namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Eternal Summoner (BG25_009): "Reborn. Deathrattle: Summon 1 Eternal Knight." Golden: "Summon a Golden Eternal Knight."
/// Reborn is a keyword (the reborn copy's deathrattle summons again). The Knight's "+4/+2 for each friendly Eternal
/// Knight that died this game" comes from its own effect (<see cref="EternalKnight"/>).
/// </summary>
[CardEffect("BG25_009")]
internal sealed class EternalSummoner : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) => fight.SummonAt(self, Tokens.EternalKnight(self.Golden));
}
