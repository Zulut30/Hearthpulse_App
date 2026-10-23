namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Bile Spitter (BG33_318): "Venomous. Rally: Give another friendly Murloc Venomous."
/// Golden: "Rally: Give 2 other friendly Murlocs Venomous." Venomous on itself is a captured keyword.
/// </summary>
/// <remarks>
/// Assumption: the Murlocs are picked at random among the other living friendly Murlocs that do not already have
/// Venomous (as random keyword-granting effects in Battlegrounds skip minions that already have the keyword); when every
/// other Murloc already has it, nothing changes.
/// </remarks>
[CardEffect("BG33_318")]
internal sealed class BileSpitter : CombatEffect
{
    public override void OnAttack(Fight fight, Unit self, Unit target)
    {
        var candidates = fight.Alive(self.Side, Tribe.Murloc, except: self).Where(u => !u.Venomous).ToList();
        foreach (var murloc in fight.RandomDistinct(candidates, self.Pick(1, 2)))
        {
            fight.GiveKeywords(murloc, Keyword.Venomous);
        }
    }
}
