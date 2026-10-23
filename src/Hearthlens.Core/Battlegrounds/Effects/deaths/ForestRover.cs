namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Forest Rover (BG31_801): "Battlecry: Your Beetles have +2/+1 this game. Deathrattle: Summon a {0}/{1} Beetle."
/// Golden: "+4/+2 ... Summon two {0}/{1} Beetles." The Battlecry happened in the tavern and is already part of the
/// Beetle stats the card shows (its script data).
/// </summary>
[CardEffect("BG31_801")]
internal sealed class ForestRover : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) => Beetles.Summon(fight, self, self.Pick(1, 2));
}
