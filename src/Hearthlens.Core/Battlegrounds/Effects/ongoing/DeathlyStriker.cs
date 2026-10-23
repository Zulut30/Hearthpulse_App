namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Deathly Striker (BG31_835): "Avenge (4): Get a random Undead. Deathrattle: Summon it from your hand for this combat
/// only." Golden: "Avenge (4): Get 2 random Undead. Deathrattle: Summon them from your hand for this combat only."
/// <para>
/// Each Avenge adds a random Undead from the public Battlegrounds pool (<see cref="UndeadPool"/>, up to the player's Tavern
/// tier, a normal minion with its printed stats) to the hand, unless the hand already holds 10 cards. The deathrattle
/// summons the Undead this Striker got in this fight that are still in the hand, left to right, where the Striker stood.
/// A full board leaves the rest in the hand. Undead the Striker got in earlier combats are in the hand too, but the log does
/// not link them to this Striker, so they are not summoned: the sim understates the deathrattle after the first combat.
/// That also means the real hand is needed, which the opponent's is not: their Striker stays "not simulated".
/// Avenge counts friendly deaths from the start of the fight (engine rule).
/// </para>
/// </summary>
[CardEffect("BG31_835")]
internal sealed class DeathlyStriker : AvengeEffect
{
    private const int MaxHandSize = 10;

    public override bool NeedsHand => true;

    public sealed class State
    {
        public readonly List<SimMinion> Got = new();
    }

    protected override int Threshold(Unit self) => 4;

    protected override void Avenge(Fight fight, Unit self)
    {
        var side = self.Side;
        var state = UnitState<State>.Of(self);
        for (var i = 0; i < self.Pick(1, 2); i++)
        {
            if (side.Hand.Count >= MaxHandSize || UndeadPool.Random(fight, side.Info.TavernTier) is not { } undead)
            {
                break;
            }

            if (fight.Tracing) fight.Trace($"avenge: {self} gets {undead.CardId} {undead.Attack}/{undead.Health}");
            side.Hand.Add(undead);
            state.Got.Add(undead);
        }
    }

    public override void Deathrattle(Fight fight, Unit self)
    {
        var state = UnitState<State>.Of(self);
        var hand = self.Side.Hand;
        var summons = new List<SimMinion>(state.Got.Count);
        foreach (var undead in state.Got)
        {
            // Another effect may have taken it from the hand already (then it is not there to summon).
            if (hand.Contains(undead))
            {
                summons.Add(undead);
            }
        }

        foreach (var summoned in SummonOrder.SummonAll(fight, self, summons))
        {
            var index = hand.FindIndex(m => ReferenceEquals(m, summoned.Spec));
            if (index >= 0)
            {
                hand.RemoveAt(index);
            }

            state.Got.RemoveAll(m => ReferenceEquals(m, summoned.Spec));
        }
    }
}
