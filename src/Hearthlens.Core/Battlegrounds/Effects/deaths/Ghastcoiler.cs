namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Ghastcoiler (BGS_008): "Deathrattle: Summon {0} random Deathrattle minions." (2; golden 4.)
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Each summon is drawn independently (repeats possible) from <see cref="GhastcoilerPool"/>: every Battlegrounds
/// pool minion with a Deathrattle except Ghastcoiler itself and the Duos-only minions, any tier, normal (not golden)
/// versions with printed stats. Assumption: the lobby's tribe rotation is not known to the simulator, so minions of
/// tribes that are out of the lobby can be drawn too.</item>
/// <item>The count is the captured TAG_SCRIPT_DATA_NUM_1 ({0}), else 2 (golden 4). Draws stop once the board is full.</item>
/// <item>A summoned minion acts only through its registered effect; one without a registered effect is plain stats
/// and keywords. This is not reflected in the "not simulated" count, which is about captured minions.</item>
/// </list>
/// </remarks>
[CardEffect("BGS_008")]
internal sealed class Ghastcoiler : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
        var pool = GhastcoilerPool.Minions;
        var count = DeathKit.Shown1(self, 2, 4);
        DeathKit.SummonInOrder(fight, self, Draw(fight, self.Side, pool, count));
    }

    // Lazy: the next draw is made only after the previous minion was summoned, and only while there is room.
    private static IEnumerable<SimMinion> Draw(Fight fight, Side side, IReadOnlyList<SimMinion> pool, int count)
    {
        for (var i = 0; i < count && !side.IsFull; i++)
        {
            yield return pool[fight.Random.Next(pool.Count)];
        }
    }
}
