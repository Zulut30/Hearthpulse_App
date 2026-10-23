using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects;
using Hearthlens.Core.Battlegrounds.Effects.Attack;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Attack;

/// <summary>
/// "Whenever a friendly minion attacks" and "after a friendly minion attacks": Prodigious Tusker, Roaring Recruiter, Cage
/// Gnawer and Deathstrider. Expected results are worked out by hand from the card text.
/// </summary>
public sealed class FriendlyAttackTests
{
    private static readonly SimMinion Token = Minion(1, 1, cardId: "TEST_TOKEN");

    private static List<(int, int)> Stats(IEnumerable<SimMinion> minions) => minions.Select(m => (m.Attack, m.Health)).ToList();

    // ------------------------------------------------------------------------------------------- Prodigious Tusker

    [Fact]
    public void Prodigious_tusker_gems_other_attackers_but_not_itself()
    {
        // A 1/1, Tusker 2/5 vs 0/4. A attacks: Tusker gems it → 2/2, hits 2 (4→2). Tusker attacks: no gem on itself, hits 2: dies.
        var result = Fight(Side(Minion(1, 1, cardId: "A"), Minion(2, 5, cardId: "BG33_430", tribes: Tribe.Quilboar)),
                           Side(Minion(0, 4, cardId: "E")), true, Effects(typeof(ProdigiousTusker)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(2, 2), (2, 5)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_prodigious_tusker_plays_two_gems()
    {
        // A 1/1, golden Tusker 4/10 vs 0/7. A gets two gems → 3/3, hits 3 (7→4); Tusker hits 4: dies.
        var result = Fight(Side(Minion(1, 1, cardId: "A"), Minion(4, 10, cardId: "BG33_430", tribes: Tribe.Quilboar, golden: true)),
                           Side(Minion(0, 7, cardId: "E")), true, Effects(typeof(ProdigiousTusker)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(3, 3), (4, 10)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Roaring Recruiter

    [Fact]
    public void Roaring_recruiter_buffs_other_attacking_dragons()
    {
        // D 1/1 Dragon, N 1/1, Recruiter 2/8 Dragon vs 0/8. D: +3/+1 → 4/2, hits 4 (8→4); N: no buff, 1 (→3);
        // Recruiter: not itself, 2 (→1); D: 7/3, kills.
        var result = Fight(
            Side(Minion(1, 1, cardId: "D", tribes: Tribe.Dragon), Minion(1, 1, cardId: "N"), Minion(2, 8, cardId: "BG29_816", tribes: Tribe.Dragon)),
            Side(Minion(0, 8, cardId: "E")),
            true, Effects(typeof(RoaringRecruiter)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(7, 3), (1, 1), (2, 8)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_roaring_recruiter_gives_six_two()
    {
        // D 1/1 Dragon, N 1/1, golden Recruiter 4/16 vs 0/12. D: 7/3, hits 7 (12→5); N 1 (→4); Recruiter 4: dies.
        var result = Fight(
            Side(Minion(1, 1, cardId: "D", tribes: Tribe.Dragon), Minion(1, 1, cardId: "N"),
                 Minion(4, 16, cardId: "BG29_816", tribes: Tribe.Dragon, golden: true)),
            Side(Minion(0, 12, cardId: "E")),
            true, Effects(typeof(RoaringRecruiter)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(7, 3), (1, 1), (4, 16)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Cage Gnawer

    [Fact]
    public void Cage_gnawer_buffs_beasts_when_any_friendly_beast_attacks_itself_included()
    {
        // B 1/1 Beast, N 1/1, Gnawer 2/7 Beast vs 0/10. B attacks: Beasts +2/+1 → B 3/2, Gnawer 4/8; hits 3 (10→7).
        // N: not a Beast, 1 (→6). Gnawer (a Beast) attacks: B 5/3, Gnawer 6/9; hits 6: dies.
        var result = Fight(
            Side(Minion(1, 1, cardId: "B", tribes: Tribe.Beast), Minion(1, 1, cardId: "N"), Minion(2, 7, cardId: "BG36_211", tribes: Tribe.Beast)),
            Side(Minion(0, 10, cardId: "E")),
            true, Effects(typeof(CageGnawer)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(5, 3), (1, 1), (6, 9)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_cage_gnawer_gives_four_two()
    {
        // B 1/1 Beast, N 1/1, golden Gnawer 4/14 vs 0/18. B: B 5/3, Gnawer 8/16, hits 5 (18→13); N 1 (→12);
        // Gnawer: B 9/5, Gnawer 12/18, hits 12: dies.
        var result = Fight(
            Side(Minion(1, 1, cardId: "B", tribes: Tribe.Beast), Minion(1, 1, cardId: "N"),
                 Minion(4, 14, cardId: "BG36_211", tribes: Tribe.Beast, golden: true)),
            Side(Minion(0, 18, cardId: "E")),
            true, Effects(typeof(CageGnawer)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(9, 5), (1, 1), (12, 18)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Deathstrider

    [Fact]
    public void Deathstrider_triggers_the_left_most_deathrattle_after_a_rally_minion_attacks()
    {
        // Ra (Rally) 1/5, DR1 0/5 and DR2 0/5 (deathrattle: summon a 1/1), Deathstrider 10/11 vs 0/1.
        // Ra attacks and kills the 0/1; after the attack (before deaths) the left-most deathrattle, DR1's, triggers once:
        // a token appears right of DR1. DR2 is not triggered.
        var result = Fight(
            Side(Minion(1, 5, cardId: "RA", mechanics: CardMechanic.Rally),
                 Minion(0, 5, cardId: "TEST_DR", mechanics: CardMechanic.Deathrattle),
                 Minion(0, 5, cardId: "TEST_DR2", mechanics: CardMechanic.Deathrattle),
                 Minion(10, 11, cardId: "BG36_208", tribes: Tribe.Beast)),
            Side(Minion(0, 1, cardId: "E")),
            true, Effects(typeof(Deathstrider), typeof(SummonToken), typeof(SummonToken2)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal(["RA", "TEST_DR", "TEST_TOKEN", "TEST_DR2", "BG36_208"], result.Ours.Select(m => m.CardId));
        Assert.Single(result.Lines("deathrattle of our TEST_DR "));
        Assert.Empty(result.Lines("deathrattle of our TEST_DR2"));
    }

    [Fact]
    public void Deathstrider_ignores_attacks_by_minions_without_rally()
    {
        // N (no Rally) 1/5 attacks and kills the 0/1: no deathrattle is triggered.
        var result = Fight(
            Side(Minion(1, 5, cardId: "N"), Minion(0, 5, cardId: "TEST_DR", mechanics: CardMechanic.Deathrattle), Minion(10, 11, cardId: "BG36_208")),
            Side(Minion(0, 1, cardId: "E")),
            true, Effects(typeof(Deathstrider), typeof(SummonToken)));

        Assert.Equal(["N", "TEST_DR", "BG36_208"], result.Ours.Select(m => m.CardId));
        Assert.Empty(result.Lines("deathrattle of"));
    }

    [Fact]
    public void Golden_deathstrider_triggers_the_deathrattle_twice()
    {
        // As above with a golden Deathstrider 20/22: DR1's deathrattle runs twice, two tokens right of it
        // (the second summoned right of DR1 too, so it lands left of the first).
        var result = Fight(
            Side(Minion(1, 5, cardId: "RA", mechanics: CardMechanic.Rally),
                 Minion(0, 5, cardId: "TEST_DR", mechanics: CardMechanic.Deathrattle),
                 Minion(20, 22, cardId: "BG36_208", tribes: Tribe.Beast, golden: true)),
            Side(Minion(0, 1, cardId: "E")),
            true, Effects(typeof(Deathstrider), typeof(SummonToken)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal(["RA", "TEST_DR", "TEST_TOKEN", "TEST_TOKEN", "BG36_208"], result.Ours.Select(m => m.CardId));
        Assert.Equal(2, result.Lines("deathrattle of our TEST_DR").Count);
    }

    [CardEffect("TEST_DR")]
    private sealed class SummonToken : CombatEffect
    {
        public override void Deathrattle(Fight fight, Unit self) => fight.SummonAt(self, Token);
    }

    [CardEffect("TEST_DR2")]
    private sealed class SummonToken2 : CombatEffect
    {
        public override void Deathrattle(Fight fight, Unit self) => fight.SummonAt(self, Token);
    }
}
