using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects.Ongoing;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Ongoing;

/// <summary>
/// Effects that use the local player's hand: Diremuck Forager (waits for space, then summons Murlocs from the hand) and
/// Deathly Striker (gets random Undead from the public pool, then summons them with its deathrattle).
/// </summary>
public sealed class HandTests
{
    private const string Forager = "BG27_556";
    private const string Striker = "BG31_835";

    private static List<(int, int)> Stats(IEnumerable<SimMinion> minions) => minions.Select(m => (m.Attack, m.Health)).ToList();

    // ------------------------------------------------------------------------------------------- Diremuck Forager

    private static SimMinion Murloc(string id, int attack, int health) => Minion(attack, health, cardId: id, tribes: Tribe.Murloc);

    private static SimSide FullBoardWithForager(bool golden = false, int foragerAttack = 4, int foragerHealth = 5, params SimMinion[] hand)
    {
        // The Forager and six 0/1 bystanders: seven minions, no space.
        var forager = Minion(foragerAttack, foragerHealth, cardId: Forager, tribes: Tribe.Murloc, golden: golden);
        return Side(forager, Minion(0, 1), Minion(0, 1), Minion(0, 1), Minion(0, 1), Minion(0, 1), Minion(0, 1)) with { Hand = hand };
    }

    [Fact]
    public void Diremuck_forager_summons_the_highest_attack_murloc_from_the_hand_once_there_is_space()
    {
        // Full board, hand: a 3/3 Murloc, a 5/2 Murloc and a 9/9 Beast. Their 1/3 kills a bystander (pick 1): space. The
        // 5/2 (highest-Attack Murloc) is summoned right of the Forager. Then our Forager (4 Attack) kills the 1/3 and takes 1.
        var ours = FullBoardWithForager(hand: [Murloc("MUR_LOW", 3, 3), Murloc("MUR_HIGH", 5, 2), Minion(9, 9, cardId: "BEAST", tribes: Tribe.Beast)]);
        var result = Fight(ours, Side(Minion(1, 3)), oursFirst: false, Effects(typeof(DiremuckForager)), 1);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(4, 4), (5, 2), (0, 1), (0, 1), (0, 1), (0, 1), (0, 1)], Stats(result.Ours));
        Assert.Equal(["summoned our MUR_HIGH 5/2 at 1"], result.Lines("summoned"));
    }

    [Fact]
    public void Golden_diremuck_forager_summons_the_two_highest_attack_murlocs_as_space_opens()
    {
        // Golden Forager 8/10, hand: Murlocs 6/2, 4/4 and 2/2. Their 1/20 kills a bystander (pick 1): the 6/2 is summoned and
        // the board is full again. Our Forager attacks (the target pick takes the second value). Their next attack (pick 2)
        // kills another bystander: the 4/4 is summoned right of the Forager, in front of the 6/2. The 2/2 stays in the hand.
        var ours = FullBoardWithForager(golden: true, foragerAttack: 8, foragerHealth: 10,
                                        hand: [Murloc("MUR_A", 6, 2), Murloc("MUR_B", 4, 4), Murloc("MUR_C", 2, 2)]);
        var result = Fight(ours, Side(Minion(1, 20)), oursFirst: false, Effects(typeof(DiremuckForager)), 1, 0, 2);

        Assert.Equal(["summoned our MUR_A 6/2 at 1", "summoned our MUR_B 4/4 at 1"], result.Lines("summoned"));
    }

    [Fact]
    public void Diremuck_forager_with_space_at_the_start_has_nothing_pending()
    {
        // Six minions only: the Start of Combat summon already happened before the fight (it is not in the hand any more).
        // The 5/2 left in the hand is not summoned when a bystander dies.
        var forager = Minion(4, 5, cardId: Forager, tribes: Tribe.Murloc);
        var ours = Side(forager, Minion(0, 1), Minion(0, 1), Minion(0, 1), Minion(0, 1), Minion(0, 1)) with { Hand = [Murloc("MUR_HIGH", 5, 2)] };
        var result = Fight(ours, Side(Minion(1, 3)), oursFirst: false, Effects(typeof(DiremuckForager)), 1);

        Assert.Empty(result.Lines("summoned"));
    }

    [Fact]
    public void Diremuck_forager_still_summons_after_it_has_died()
    {
        // Their 1/20 kills the Forager itself first (pick 0): the pending summon goes on, to the right end of the board.
        var ours = FullBoardWithForager(foragerAttack: 0, foragerHealth: 1, hand: [Murloc("MUR_HIGH", 5, 2)]);
        var result = Fight(ours, Side(Minion(1, 20)), oursFirst: false, Effects(typeof(DiremuckForager)), 0);

        Assert.Equal(["summoned our MUR_HIGH 5/2 at 6"], result.Lines("summoned"));
    }

    [Fact]
    public void Diremuck_forager_needs_a_murloc_in_the_hand()
    {
        var ours = FullBoardWithForager(hand: [Minion(9, 9, cardId: "BEAST", tribes: Tribe.Beast)]);
        var result = Fight(ours, Side(Minion(1, 3)), oursFirst: false, Effects(typeof(DiremuckForager)), 1);

        Assert.Empty(result.Lines("summoned"));
    }

    [Fact]
    public void Diremuck_forager_breaks_ties_between_murlocs_at_random()
    {
        // Two 5-Attack Murlocs: the tie-break pick chooses (1 keeps the first, 0 takes the second).
        var hand = new[] { Murloc("MUR_FIRST", 5, 2), Murloc("MUR_SECOND", 5, 3) };
        var first = Fight(FullBoardWithForager(hand: hand), Side(Minion(1, 3)), oursFirst: false, Effects(typeof(DiremuckForager)), 1, 1);
        var second = Fight(FullBoardWithForager(hand: hand), Side(Minion(1, 3)), oursFirst: false, Effects(typeof(DiremuckForager)), 1, 0);

        Assert.Equal(["summoned our MUR_FIRST 5/2 at 1"], first.Lines("summoned"));
        Assert.Equal(["summoned our MUR_SECOND 5/3 at 1"], second.Lines("summoned"));
    }

    [Fact]
    public void Diremuck_forager_of_the_opponent_never_sees_a_hand()
    {
        // The opponent's hand is hidden: even a hand put into their SimSide is ignored, so nothing is summoned.
        var theirs = FullBoardWithForager(hand: [Murloc("MUR_HIGH", 5, 2)]);
        var result = Fight(Side(Minion(1, 3)), theirs, oursFirst: true, Effects(typeof(DiremuckForager)), 1);

        Assert.Empty(result.Lines("summoned"));
    }

    // ------------------------------------------------------------------------------------------- Deathly Striker

    private static SimMinion StrikerMinion(bool golden = false) =>
        Minion(0, 3, cardId: Striker, tribes: Tribe.Undead, golden: golden, mechanics: CardMechanic.Avenge | CardMechanic.Deathrattle);

    /// <summary>The Striker, four 0/1 minions that die first, and a 0/50 bystander.</summary>
    private static SimSide StrikerBoard(bool golden = false, int tavernTier = 6, params SimMinion[] hand) =>
        Side(StrikerMinion(golden), Minion(0, 1), Minion(0, 1), Minion(0, 1), Minion(0, 1), Minion(0, 50)) with
        {
            TavernTier = tavernTier,
            Hand = hand,
        };

    // Their 3/200 kills a 0/1 per attack (pick 1 = the minion right of the Striker, four times); the fourth death is the
    // Avenge. Pick(s) for the random Undead follow; the last pick (0) sends the next attack at the Striker (3 health).
    private static int[] Picks(params int[] undead) => [1, 1, 1, 1, .. undead, 0];

    [Fact]
    public void Deathly_striker_gets_an_undead_after_four_deaths_and_summons_it_when_it_dies()
    {
        // Tier 6, pick = the Forsaken Weaver (3/8). Only the Undead it got is summoned, not the old card in the hand.
        var oldCard = Minion(1, 1, cardId: "OLD_UNDEAD", tribes: Tribe.Undead);
        var ours = StrikerBoard(hand: oldCard);
        var weaver = UndeadPool.IndexOf("BG34_692", 6);
        var result = Fight(ours, Side(Minion(3, 200)), oursFirst: false, Effects(typeof(DeathlyStriker)), Picks(weaver));

        Assert.Contains("avenge: BG31_835 0/3 gets BG34_692 3/8", result.Log);
        Assert.Equal(["summoned our BG34_692 3/8 at 0"], result.Lines("summoned"));
    }

    [Fact]
    public void Golden_deathly_striker_gets_two_undead_and_summons_them_left_to_right()
    {
        // Golden: Avenge (4) gets 2 Undead (Forsaken Weaver 3/8, then Nerubian Deathswarmer 1/4); the deathrattle summons
        // them in that order where the Striker stood (the 0/50 on its right is the anchor).
        var ours = StrikerBoard(golden: true);
        var picks = Picks(UndeadPool.IndexOf("BG34_692", 6), UndeadPool.IndexOf("BG25_011", 6));
        var result = Fight(ours, Side(Minion(3, 200)), oursFirst: false, Effects(typeof(DeathlyStriker)), picks);

        Assert.Equal(["summoned our BG34_692 3/8 at 0", "summoned our BG25_011 1/4 at 1"], result.Lines("summoned"));
    }

    [Fact]
    public void Deathly_striker_only_gets_undead_from_the_pool_up_to_the_tavern_tier()
    {
        // Tier 2 pool: Risen Rider, Harmless Bonehead, Eternal Knight, Nerubian Deathswarmer, Scarlet Skull. A pick past the end
        // takes the last one, the 2/1 Scarlet Skull with Reborn (a tier 7 minion cannot come out).
        var ours = StrikerBoard(tavernTier: 2);
        var result = Fight(ours, Side(Minion(3, 200)), oursFirst: false, Effects(typeof(DeathlyStriker)), Picks(99));

        Assert.Equal(["summoned our BG25_022 2/1 Reborn at 0"], result.Lines("summoned"));
        Assert.Equal(-1, UndeadPool.IndexOf("BG31_999", 5));
        Assert.True(UndeadPool.IndexOf("BG31_999", 7) > 0);
    }

    [Fact]
    public void Deathly_striker_gets_nothing_before_the_fourth_death()
    {
        // Three 0/1 minions die, then the Striker: no Avenge, nothing in the hand to summon.
        var ours = Side(StrikerMinion(), Minion(0, 1), Minion(0, 1), Minion(0, 1), Minion(0, 50)) with { TavernTier = 6 };
        var result = Fight(ours, Side(Minion(3, 200)), oursFirst: false, Effects(typeof(DeathlyStriker)), 1, 1, 1, 0);

        Assert.Empty(result.Lines("summoned"));
        Assert.DoesNotContain(result.Log, l => l.StartsWith("avenge:", StringComparison.Ordinal));
    }

    [Fact]
    public void Deathly_striker_gets_nothing_when_the_hand_is_full()
    {
        // Ten cards in the hand: nothing can be added (and no random pick is spent on it).
        var hand = Enumerable.Range(0, 10).Select(i => Minion(1, 1, cardId: $"OLD_{i}", tribes: Tribe.Beast)).ToArray();
        var result = Fight(StrikerBoard(hand: hand), Side(Minion(3, 200)), oursFirst: false, Effects(typeof(DeathlyStriker)), 1, 1, 1, 1, 0);

        Assert.Empty(result.Lines("summoned"));
        Assert.DoesNotContain(result.Log, l => l.StartsWith("avenge:", StringComparison.Ordinal));
    }

    [Fact]
    public void Deathly_striker_of_the_opponent_uses_its_own_pool_pick_without_a_hand_from_the_log()
    {
        // The same fight on the opponent's side: the Undead it gets come from the pool (no hidden card is read), and are
        // summoned the same way.
        var theirs = StrikerBoard();
        var weaver = UndeadPool.IndexOf("BG34_692", 6);
        var result = Fight(Side(Minion(3, 200)), theirs, oursFirst: true, Effects(typeof(DeathlyStriker)), Picks(weaver));

        Assert.Equal(["summoned their BG34_692 3/8 at 0"], result.Lines("summoned"));
    }
}
