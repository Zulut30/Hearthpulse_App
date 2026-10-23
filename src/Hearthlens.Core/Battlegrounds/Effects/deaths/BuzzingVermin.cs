namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Buzzing Vermin (BG31_803): "Taunt. Deathrattle: Summon a {0}/{1} Beetle." (2/2 plus the player's Beetle bonus).
/// Golden: "Summon two {0}/{1} Beetles." Taunt is a keyword the engine applies itself.
/// </summary>
[CardEffect("BG31_803")]
internal sealed class BuzzingVermin : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) => Beetles.Summon(fight, self, self.Pick(1, 2));
}
