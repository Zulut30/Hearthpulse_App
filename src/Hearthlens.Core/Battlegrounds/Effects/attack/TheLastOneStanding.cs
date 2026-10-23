namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// The Last One Standing (BG34_320): "Rally: Give a friendly minion of each type +15/+15 permanently."
/// Golden: "... +15/+15 permanently, twice." ("Permanently" only matters after combat.)
/// </summary>
/// <remarks>
/// Assumption (the exact selection rule is not on the card): for each minion type in a fixed order (Beast, Demon,
/// Dragon, Elemental, Mech, Murloc, Naga, Pirate, Quilboar, Undead, Aberration) one random living friendly minion of that
/// type is chosen, itself included; a minion is chosen at most once per trigger, and minions with fewer types are
/// preferred so that a single-type minion covers its type before a multi-type one (Amalgam-style, or this minion,
/// which has every type). This follows how "a friendly minion of each type" effects behave in Battlegrounds as far as
/// known (one buff per minion). The chosen minions then get +15/+15. Golden: the whole effect runs twice, with fresh picks.
/// </remarks>
[CardEffect("BG34_320")]
internal sealed class TheLastOneStanding : CombatEffect
{
    private static readonly Tribe[] Types =
    [
        Tribe.Beast, Tribe.Demon, Tribe.Dragon, Tribe.Elemental, Tribe.Mech, Tribe.Murloc,
        Tribe.Naga, Tribe.Pirate, Tribe.Quilboar, Tribe.Undead, Tribe.Aberration,
    ];

    public override void OnAttack(Fight fight, Unit self, Unit target)
    {
        for (var time = 0; time < self.Pick(1, 2); time++)
        {
            foreach (var unit in Choose(fight, self))
            {
                fight.Buff(unit, 15, 15);
            }
        }
    }

    private static List<Unit> Choose(Fight fight, Unit self)
    {
        var chosen = new List<Unit>();
        foreach (var type in Types)
        {
            var candidates = fight.Alive(self.Side, type).Where(u => !chosen.Contains(u)).ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            var fewest = candidates.Min(TypeCount);
            if (fight.RandomOf(candidates.Where(u => TypeCount(u) == fewest).ToList()) is { } pick)
            {
                chosen.Add(pick);
            }
        }

        return chosen;
    }

    private static int TypeCount(Unit unit) => System.Numerics.BitOperations.PopCount((uint)(unit.Tribes & Tribe.All));
}
