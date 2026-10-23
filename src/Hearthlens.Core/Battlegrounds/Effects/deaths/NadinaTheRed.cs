namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Nadina the Red (BGS_040): "Deathrattle: Give 3 friendly Dragons Divine Shield." Golden: "Give 6 friendly Dragons
/// Divine Shield."
/// </summary>
/// <remarks>
/// Assumption (usual Battlegrounds behaviour for "give friendly minions Divine Shield"): random living friendly Dragons
/// that do not have Divine Shield yet; all of them when there are 3 (golden 6) or fewer.
/// </remarks>
[CardEffect("BGS_040")]
internal sealed class NadinaTheRed : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
        var candidates = fight.Alive(self.Side, Tribe.Dragon, except: self);
        candidates.RemoveAll(static unit => unit.DivineShield);
        foreach (var unit in DeathKit.Choose(fight, candidates, self.Pick(3, 6)))
        {
            fight.GiveKeywords(unit, Keyword.DivineShield);
        }
    }
}
