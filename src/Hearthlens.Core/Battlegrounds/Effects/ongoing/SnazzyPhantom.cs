namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Snazzy Phantom (BG36_515): "After a friendly minion is Reborn, give stats equal to its Attack to your right-most
/// Undead." Golden: "double its Attack". "Its Attack" is the reborn copy's Attack once its summon triggers resolved; the
/// right-most living friendly Undead may be the copy itself or the Phantom.
/// </summary>
[CardEffect("BG36_515")]
internal sealed class SnazzyPhantom : CombatEffect
{
    public override void AfterFriendlyReborn(Fight fight, Unit self, Unit reborn)
    {
        var amount = reborn.Attack * self.Pick(1, 2);
        if (amount <= 0)
        {
            return;
        }

        var board = self.Side.Minions;
        for (var i = board.Count - 1; i >= 0; i--)
        {
            var unit = board[i];
            if (unit.IsAlive && unit.Is(Tribe.Undead))
            {
                fight.Buff(unit, amount, amount);
                return;
            }
        }
    }
}
