using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects.Attack;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Attack;

/// <summary>
/// Rally stat buffs: Glim Guardian, Tusked Camper, Wolf Pup, Bonker, Heroic Underdog, Razorfen Vineweaver and The Last One
/// Standing. Enemies with 0 Attack never attack, so our minions keep attacking until they die; expected stats are worked
/// out by hand from the card text in each comment.
/// </summary>
public sealed class RallyBuffTests
{
    private static SimSide WithTags(SimSide side, params (string Tag, int Value)[] tags) =>
        side with { Tags = tags.ToDictionary(t => t.Tag, t => t.Value) };

    private static List<(int, int)> Stats(IEnumerable<SimMinion> minions) => minions.Select(m => (m.Attack, m.Health)).ToList();

    // ------------------------------------------------------------------------------------------- Glim Guardian

    [Fact]
    public void Glim_guardian_gains_two_attack_every_time_it_attacks()
    {
        // 1/4 vs 0/10: attacks for 3 (10→7), 5 (7→2), 7 (dies). Ends 7/4 after three attacks.
        var result = Fight(Side(Minion(1, 4, cardId: "BG29_888", tribes: Tribe.Dragon)), Side(Minion(0, 10, cardId: "E")),
                           true, Effects(typeof(GlimGuardian)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(7, 4)], Stats(result.Ours));
        Assert.Equal(3, result.Lines("our BG29_888").Count(l => l.Contains(" attacks ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Golden_glim_guardian_gains_four_attack()
    {
        // Golden 2/8 vs 0/20: attacks for 6 (20→14), 10 (14→4), 14 (dies). Ends 14/8.
        var result = Fight(Side(Minion(2, 8, cardId: "BG29_888", tribes: Tribe.Dragon, golden: true)), Side(Minion(0, 20, cardId: "E")),
                           true, Effects(typeof(GlimGuardian)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(14, 8)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Tusked Camper

    [Fact]
    public void Tusked_camper_plays_a_default_blood_gem_on_itself()
    {
        // No Blood Gem tags (e.g. the opponent's dummy player): +1/+1 per gem. 2/3 vs 0/10: 3/4 hits (10→7), 4/5 (7→3),
        // 5/6 (dies). Ends 5/6.
        var result = Fight(Side(Minion(2, 3, cardId: "BG33_886", tribes: Tribe.Quilboar)), Side(Minion(0, 10, cardId: "E")),
                           true, Effects(typeof(TuskedCamper)));

        Assert.Equal([(5, 6)], Stats(result.Ours));
    }

    [Fact]
    public void Tusked_camper_uses_the_players_blood_gem_bonus()
    {
        // Bonus tags +2/+1 on top of the gem's +1/+1: one gem gives +3/+2. 2/3 → 5/5, which kills the 0/5 at once.
        var ours = WithTags(Side(Minion(2, 3, cardId: "BG33_886", tribes: Tribe.Quilboar)),
                            (BloodGems.AttackTag, 2), (BloodGems.HealthTag, 1));
        var result = Fight(ours, Side(Minion(0, 5, cardId: "E")), true, Effects(typeof(TuskedCamper)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(5, 5)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_tusked_camper_plays_two_blood_gems()
    {
        // Golden 4/6 + two +1/+1 gems = 6/8, which kills the 0/6 in one hit.
        var result = Fight(Side(Minion(4, 6, cardId: "BG33_886", tribes: Tribe.Quilboar, golden: true)), Side(Minion(0, 6, cardId: "E")),
                           true, Effects(typeof(TuskedCamper)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(6, 8)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Wolf Pup

    [Fact]
    public void Wolf_pup_gives_its_other_minions_four_one()
    {
        // Pup 3/6, A 1/1, B 2/2 vs 0/20. Pup: A 5/2, B 6/3, hits 3 (20→17); A 5 (→12); B 6 (→6);
        // Pup: A 9/3, B 10/4, hits 3 (→3); A 9 kills. The Pup itself is not buffed.
        var result = Fight(
            Side(Minion(3, 6, cardId: "BG36_207", tribes: Tribe.Beast), Minion(1, 1, cardId: "A"), Minion(2, 2, cardId: "B")),
            Side(Minion(0, 20, cardId: "E")),
            true, Effects(typeof(WolfPup)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(3, 6), (9, 3), (10, 4)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_wolf_pup_gives_eight_two()
    {
        // Golden Pup 6/12, A 1/1, B 2/2 vs 0/40. Pup: A 9/3, B 10/4, hits 6 (40→34); A 9 (→25); B 10 (→15);
        // Pup: A 17/5, B 18/6, hits 6 (→9); A 17 kills.
        var result = Fight(
            Side(Minion(6, 12, cardId: "BG36_207", tribes: Tribe.Beast, golden: true), Minion(1, 1, cardId: "A"), Minion(2, 2, cardId: "B")),
            Side(Minion(0, 40, cardId: "E")),
            true, Effects(typeof(WolfPup)));

        Assert.Equal([(6, 12), (17, 5), (18, 6)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Bonker

    [Fact]
    public void Bonker_plays_a_blood_gem_on_its_other_minions_on_both_windfury_attacks()
    {
        // Bonker 2/7 Windfury, A 1/1 vs 0/6. Swing 1: A 2/2, hit 2 (6→4); swing 2: A 3/3, hit 2 (→2); A hits 3: dies.
        var result = Fight(
            Side(Minion(2, 7, Keyword.Windfury, "BG20_104", Tribe.Quilboar), Minion(1, 1, cardId: "A")),
            Side(Minion(0, 6, cardId: "E")),
            true, Effects(typeof(Bonker)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(2, 7), (3, 3)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_bonker_plays_two_blood_gems()
    {
        // Golden 4/14 Windfury, A 1/1 vs 0/10. Swing 1: A 3/3, hit 4 (10→6); swing 2: A 5/5, hit 4 (→2); A hits 5: dies.
        var result = Fight(
            Side(Minion(4, 14, Keyword.Windfury, "BG20_104", Tribe.Quilboar, golden: true), Minion(1, 1, cardId: "A")),
            Side(Minion(0, 10, cardId: "E")),
            true, Effects(typeof(Bonker)));

        Assert.Equal([(4, 14), (5, 5)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Heroic Underdog

    [Fact]
    public void Heroic_underdog_gains_the_targets_attack_before_damage()
    {
        // 1/10 Stealth vs 5/6: Rally +5 → 6/10, kills the 5/6 and takes 5 → 6/5.
        var result = Fight(Side(Minion(1, 10, Keyword.Stealth, "BG34_604")), Side(Minion(5, 6, cardId: "E")), true, Effects(typeof(HeroicUnderdog)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(6, 5)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_heroic_underdog_gains_double_the_targets_attack()
    {
        // Golden 2/20 vs 5/12: Rally +10 → 12/20, kills the 5/12 and takes 5 → 12/15.
        var result = Fight(Side(Minion(2, 20, Keyword.Stealth, "BG34_604", golden: true)), Side(Minion(5, 12, cardId: "E")), true,
                           Effects(typeof(HeroicUnderdog)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(12, 15)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Razorfen Vineweaver

    [Fact]
    public void Razorfen_vineweaver_plays_four_blood_gems_on_itself()
    {
        // 4/4 + four +1/+1 gems = 8/8, which kills the 0/8 in one hit.
        var result = Fight(Side(Minion(4, 4, cardId: "BG33_883", tribes: Tribe.Quilboar)), Side(Minion(0, 8, cardId: "E")), true,
                           Effects(typeof(RazorfenVineweaver)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(8, 8)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_razorfen_vineweaver_plays_eight_blood_gems()
    {
        // Golden 8/8 + eight gems = 16/16, which kills the 0/16 in one hit.
        var result = Fight(Side(Minion(8, 8, cardId: "BG33_883", tribes: Tribe.Quilboar, golden: true)), Side(Minion(0, 16, cardId: "E")), true,
                           Effects(typeof(RazorfenVineweaver)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(16, 16)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- The Last One Standing

    [Fact]
    public void Last_one_standing_buffs_one_minion_of_each_type()
    {
        // Board: TLOS 15/15 (every type), N 1/1 (no type), B 1/1 Beast; enemy 0/40.
        // Rally: Beast → B (single-type minions are preferred over TLOS), Demon → TLOS (the only one left), no other type
        // has an unchosen minion. B 16/16, TLOS 30/30; N has no type and gets nothing.
        // TLOS hits 30 (40→10), N 1 (→9), B 16: dies.
        var result = Fight(
            Side(Minion(15, 15, cardId: "BG34_320", tribes: Tribe.All), Minion(1, 1, cardId: "N"), Minion(1, 1, cardId: "B", tribes: Tribe.Beast)),
            Side(Minion(0, 40, cardId: "E")),
            true, Effects(typeof(TheLastOneStanding)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(30, 30), (1, 1), (16, 16)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_last_one_standing_buffs_twice()
    {
        // Golden 30/30, N 1/1, B 1/1 Beast vs 0/100. Rally twice: B 31/31, TLOS 60/60. Hits 60 (→40), N 1 (→39), B 31 (→8).
        // TLOS attacks again: twice more, B 61/61, TLOS 90/90; hits 90: dies.
        var result = Fight(
            Side(Minion(30, 30, cardId: "BG34_320", tribes: Tribe.All, golden: true), Minion(1, 1, cardId: "N"), Minion(1, 1, cardId: "B", tribes: Tribe.Beast)),
            Side(Minion(0, 100, cardId: "E")),
            true, Effects(typeof(TheLastOneStanding)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(90, 90), (1, 1), (61, 61)], Stats(result.Ours));
    }
}
