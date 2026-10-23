namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Deathstrider (BG36_208): "After a friendly Rally minion attacks, trigger your left-most Deathrattle."
/// Golden: "... trigger your left-most Deathrattle twice."
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>"A friendly Rally minion" is any friendly minion whose card has Rally (<see cref="Unit.IsRally"/>), including Rally
/// cards whose Rally only matters in the tavern. The trigger needs the attack to have happened: an attack cancelled
/// before damage (its target died to a Rally) does not count.</item>
/// <item>Assumption: "your left-most Deathrattle" is the left-most friendly minion on the board with a deathrattle
/// (<see cref="Unit.HasDeathrattle"/>), a mortally wounded one included (it is still in play until the death phase).
/// Its deathrattles run with the usual extra triggers (Titus Rivendare and similar), summons going to its right.
/// A deathrattle with no registered effect does nothing (that minion is counted as not simulated anyway).</item>
/// </list>
/// </remarks>
[CardEffect("BG36_208")]
internal sealed class Deathstrider : CombatEffect
{
    public override void AfterFriendlyAttack(Fight fight, Unit self, Unit attacker, Unit target, AttackOutcome outcome)
    {
        if (attacker.IsRally && LeftMostDeathrattle(self.Side) is { } unit)
        {
            fight.TriggerDeathrattles(unit, self.Pick(1, 2));
        }
    }

    private static Unit? LeftMostDeathrattle(Side side)
    {
        foreach (var unit in side.Minions)
        {
            if (unit.HasDeathrattle)
            {
                return unit;
            }
        }

        return null;
    }
}
