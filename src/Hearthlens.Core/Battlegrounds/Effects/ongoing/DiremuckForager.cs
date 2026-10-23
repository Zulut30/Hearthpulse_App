namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Diremuck Forager (BG27_556): "Start of Combat: When you have space, summon the highest-Attack Murloc from your hand for
/// this combat only." Golden: "the two highest-Attack Murlocs".
/// <para>
/// The Start of Combat part has resolved when the boards are captured: with space the Murloc is already on the board (and no
/// longer in the hand). What is left is the "when you have space" wait: if the board was full at the start of the fight
/// (7 minions), the summon is still pending, and it happens as soon as a friendly minion has died and there is room
/// (<see cref="PendingMurlocs"/>), even after the Forager itself died. The Murloc is summoned right of the Forager while it
/// is on the board, else at the right end. Needs the hand, which only the local player's is known: the opponent's Forager
/// stays "not simulated". With a golden Forager and a full board the sim assumes none of the two was summoned yet.
/// </para>
/// </summary>
[CardEffect("BG27_556")]
internal sealed class DiremuckForager : CombatEffect
{
    public override bool NeedsHand => true;

    public override void OnFightStart(Fight fight, Unit self)
    {
        if (!self.Side.IsFull)
        {
            return;
        }

        var holder = PlayerEffects.Attach(self.Side, self.CardId, PendingMurlocs.Instance);
        var state = UnitState<PendingMurlocs.State>.Of(holder);
        state.Remaining = self.Pick(1, 2);
        state.Source = self;
    }
}

/// <summary>The pending "when you have space" summon of a Diremuck Forager, kept as a player-level effect.</summary>
internal sealed class PendingMurlocs : CombatEffect
{
    public static readonly PendingMurlocs Instance = new();

    public sealed class State
    {
        public int Remaining;
        public Unit? Source;
    }

    public override void AfterFriendlyDeath(Fight fight, Unit self, Unit dead)
    {
        var state = UnitState<State>.Of(self);
        var side = self.Side;
        while (state.Remaining > 0 && !side.IsFull)
        {
            var hand = side.Hand;
            var best = Best(fight, hand);
            if (best < 0)
            {
                return;
            }

            var murloc = hand[best];
            var unit = state.Source is { IsOnBoard: true } source
                ? fight.SummonAt(source, murloc)
                : fight.Summon(side, murloc, side.Count);
            if (unit is null)
            {
                return;
            }

            hand.RemoveAt(best);
            state.Remaining--;
        }
    }

    /// <summary>Index of the highest-Attack Murloc in the hand (random among equals), or -1.</summary>
    private static int Best(Fight fight, List<SimMinion> hand)
    {
        var best = -1;
        var ties = 0;
        for (var i = 0; i < hand.Count; i++)
        {
            if ((hand[i].Tribes & Tribe.Murloc) == 0)
            {
                continue;
            }

            if (best < 0 || hand[i].Attack > hand[best].Attack)
            {
                best = i;
                ties = 1;
            }
            else if (hand[i].Attack == hand[best].Attack && fight.Random.Next(++ties) == 0)
            {
                best = i;
            }
        }

        return best;
    }
}
