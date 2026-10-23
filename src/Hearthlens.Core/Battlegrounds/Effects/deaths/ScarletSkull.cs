namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Scarlet Skull (BG25_022): "Reborn. Deathrattle: Give a friendly Undead +1/+2." Golden: "+2/+4."
/// </summary>
/// <remarks>
/// A random living friendly Undead. The deathrattle resolves before Reborn, so the Skull's own reborn copy cannot get the
/// first buff; the copy has the same card, so its deathrattle runs again when it dies. The text says "a friendly
/// Undead", not "another": when the deathrattle is triggered while the Skull is alive, it may pick itself.
/// </remarks>
[CardEffect("BG25_022")]
internal sealed class ScarletSkull : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
        if (fight.RandomFriendly(self, Tribe.Undead, includeSelf: true) is { } undead)
        {
            fight.Buff(undead, self.Pick(1, 2), self.Pick(2, 4));
        }
    }
}
