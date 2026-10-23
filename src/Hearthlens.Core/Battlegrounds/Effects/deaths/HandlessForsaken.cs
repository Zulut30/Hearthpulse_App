namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Handless Forsaken (BG25_010): "Deathrattle: Summon a 2/1 Hand with Reborn." Golden: "Summon two 2/1 Hands with
/// Reborn" (normal Hands).
/// </summary>
[CardEffect("BG25_010")]
internal sealed class HandlessForsaken : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) => fight.SummonAt(self, Tokens.HelpingHand(), self.Pick(1, 2));
}
