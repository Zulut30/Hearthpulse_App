namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>Small helpers shared by the deathrattle effects of this folder.</summary>
internal static class DeathKit
{
    /// <summary>
    /// The numbers a card shows as {0}/{1} in its text: the minion's captured TAG_SCRIPT_DATA_NUM_1/2 (the client fills
    /// the text from them, so they include scaling), else the printed values of the normal or golden card. Minions made
    /// in combat (tokens, reborn copies, Ghastcoiler's summons) carry no script data and use the printed values.
    /// </summary>
    public static (int First, int Second) Shown(Unit unit, (int, int) normal, (int, int) golden)
    {
        var spec = unit.Spec;
        return spec.ScriptData1 > 0 || spec.ScriptData2 > 0 ? (spec.ScriptData1, spec.ScriptData2) : unit.Golden ? golden : normal;
    }

    /// <summary>The number a card shows as {0}: its captured TAG_SCRIPT_DATA_NUM_1, else the printed value.</summary>
    public static int Shown1(Unit unit, int normal, int golden) => unit.Spec.ScriptData1 > 0 ? unit.Spec.ScriptData1 : unit.Pick(normal, golden);

    /// <summary>Buffs every living friendly minion of <paramref name="tribe"/> (<see cref="Tribe.All"/>: every one).</summary>
    public static void BuffAll(Fight fight, Side side, Tribe tribe, int attack, int health)
    {
        foreach (var unit in fight.Alive(side, tribe))
        {
            fight.Buff(unit, attack, health);
        }
    }

    /// <summary>
    /// Summons different minions one after the other where <paramref name="place"/> is, left to right: in its old slot when
    /// it has died, else to its right (each new minion right of the previous one; two plain
    /// <see cref="Fight.SummonAt(Unit, SimMinion)"/> calls from a living minion would reverse them). Stops when the board is full.
    /// </summary>
    public static void SummonInOrder(Fight fight, Unit place, IEnumerable<SimMinion> minions)
    {
        var onBoard = place.IsOnBoard;
        var anchor = place;
        foreach (var minion in minions)
        {
            var summoned = fight.SummonAt(onBoard ? anchor : place, minion);
            if (summoned is null)
            {
                return;
            }

            anchor = summoned;
        }
    }

    /// <summary>
    /// <paramref name="count"/> different random candidates (board order); all of them, without a random pick, when there
    /// are no more than <paramref name="count"/>.
    /// </summary>
    public static IReadOnlyList<Unit> Choose(Fight fight, List<Unit> candidates, int count) =>
        candidates.Count <= count ? candidates : fight.RandomDistinct(candidates, count);
}
