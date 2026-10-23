namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Cord Puller (BG29_611): "Divine Shield. Deathrattle: Summon a 1/1 Microbot." Golden: "Summon a 2/2 Microbot"
/// (the golden Microbot).
/// </summary>
[CardEffect("BG29_611")]
internal sealed class CordPuller : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) => fight.SummonAt(self, Tokens.Microbot(self.Golden));
}
