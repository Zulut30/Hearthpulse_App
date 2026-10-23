namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Expert Aviator (BG34_140): "Rally: Summon the highest-Attack Murloc from your hand for this combat only."
/// Golden: "Summon the 2 highest-Attack Murlocs from your hand for this combat only."
/// </summary>
/// <remarks>
/// Needs the owner's hand, so it is simulated for the local player only: the opponent's hand is hidden
/// (<see cref="Side.Hand"/> is empty for them) and their Aviator stays "not simulated" (<see cref="NeedsHand"/>).
/// The Murloc leaves the (per-fight copy of the) hand and is summoned to the right of the Aviator, before damage, with
/// the stats it has in hand; the golden card's second Murloc goes to the right of the first. Ties on Attack are broken at
/// random. With a full board nothing is summoned and the Murloc stays in hand.
/// </remarks>
[CardEffect("BG34_140")]
internal sealed class ExpertAviator : CombatEffect
{
    public override bool NeedsHand => true;

    public override void OnAttack(Fight fight, Unit self, Unit target)
    {
        var hand = self.Side.Hand;
        var place = self;
        for (var n = 0; n < self.Pick(1, 2) && !self.Side.IsFull; n++)
        {
            var index = HighestAttackMurloc(fight, hand);
            if (index < 0)
            {
                return;
            }

            var murloc = hand[index];
            hand.RemoveAt(index);
            if (fight.SummonAt(place.IsOnBoard ? place : self, murloc) is not { } summoned)
            {
                return;
            }

            place = summoned;
        }
    }

    private static int HighestAttackMurloc(Fight fight, List<SimMinion> hand)
    {
        int best = -1, ties = 0;
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
