namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Mummifier (BG28_309): "Deathrattle: Give a different friendly Undead Reborn." Golden: "Give 2 different friendly
/// Undead Reborn."
/// </summary>
/// <remarks>
/// Assumption (usual Battlegrounds behaviour for "give a random friendly minion keyword X"): the pick is made among
/// living friendly Undead other than the Mummifier that do not already have Reborn; with no such Undead nothing happens.
/// The Reborn is lost on the reborn copy (the engine brings back the printed card).
/// </remarks>
[CardEffect("BG28_309")]
internal sealed class Mummifier : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
        var candidates = fight.Alive(self.Side, Tribe.Undead, except: self);
        candidates.RemoveAll(static unit => unit.Reborn);
        foreach (var unit in DeathKit.Choose(fight, candidates, self.Pick(1, 2)))
        {
            fight.GiveKeywords(unit, Keyword.Reborn);
        }
    }
}
