using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects.Attack;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Attack;

/// <summary>
/// After-attack damage, cleave and Rally splash: Wildfire Elemental, De-volition-ist, Blade Collector and Obsidian Ravager.
/// Expected results are worked out by hand from the card text; scripted picks are the Random.Next results in call order
/// (then 0). Damage log lines show the victim after the damage and the source as it is at that moment.
/// </summary>
public sealed class DamageEffectTests
{
    private static List<(int, int)> Stats(IEnumerable<SimMinion> minions) => minions.Select(m => (m.Attack, m.Health)).ToList();

    // ------------------------------------------------------------------------------------------- Wildfire Elemental

    [Fact]
    public void Wildfire_elemental_deals_excess_damage_to_one_adjacent_enemy()
    {
        // Wildfire 6/3 vs L 0/10, T 0/2, R 0/10. Picks: target T (1 of 3), then the adjacent enemy L (0 of 2).
        // 6 damage kills the 0/2 with 4 excess: L takes 4. R is not hit by the excess.
        var result = Fight(
            Side(Minion(6, 3, cardId: "BGS_126", tribes: Tribe.Elemental)),
            Side(Minion(0, 10, cardId: "L"), Minion(0, 2, cardId: "T"), Minion(0, 10, cardId: "R")),
            true, Effects(typeof(WildfireElemental)), picks: [1, 0]);

        Assert.Equal(1, result.Outcome);
        var excess = result.IndexOf("their L 0/6 takes 4 from our BGS_126 6/3");
        Assert.True(excess > result.IndexOf("their T 0/-4 takes 6"), string.Join("\n", result.Log));
        Assert.DoesNotContain(result.Log, l => l.StartsWith("their R 0/6 takes 4", StringComparison.Ordinal));
    }

    [Fact]
    public void Golden_wildfire_elemental_hits_both_adjacent_enemies()
    {
        // Golden 12/6 vs L 0/20, T 0/2, R 0/20; target T (pick 1): 10 excess to both L and R.
        var result = Fight(
            Side(Minion(12, 6, cardId: "BGS_126", tribes: Tribe.Elemental, golden: true)),
            Side(Minion(0, 20, cardId: "L"), Minion(0, 2, cardId: "T"), Minion(0, 20, cardId: "R")),
            true, Effects(typeof(WildfireElemental)), picks: [1]);

        Assert.Contains("their L 0/10 takes 10 from our BGS_126(G) 12/6", result.Log);
        Assert.Contains("their R 0/10 takes 10 from our BGS_126(G) 12/6", result.Log);
    }

    [Fact]
    public void Wildfire_elemental_does_nothing_without_a_kill()
    {
        // 6/3 vs L 0/10, T 0/9, R 0/10, target T: it survives (0/3), so no excess damage; the next line is already
        // its next attack (on L, pick 0 once the script runs out).
        var result = Fight(
            Side(Minion(6, 3, cardId: "BGS_126", tribes: Tribe.Elemental)),
            Side(Minion(0, 10, cardId: "L"), Minion(0, 9, cardId: "T"), Minion(0, 10, cardId: "R")),
            true, Effects(typeof(WildfireElemental)), picks: [1]);

        Assert.Equal(
            ["our BGS_126 6/3 attacks their T 0/9", "their T 0/3 takes 6 from our BGS_126 6/3", "our BGS_126 6/3 attacks their L 0/10"],
            result.Log.Take(3));
    }

    // ------------------------------------------------------------------------------------------- De-volition-ist

    [Fact]
    public void De_volitionist_hits_the_highest_health_enemy_after_attacking()
    {
        // De-volition-ist 4/8 vs A 0/5, B 0/9. It attacks A (pick 0): A → 0/1. After the attack the highest-Health enemy
        // is B (9 > 1): B takes 4 (its Attack) → 0/5.
        var result = Fight(
            Side(Minion(4, 8, cardId: "BG36_102", tribes: Tribe.Aberration)),
            Side(Minion(0, 5, cardId: "A"), Minion(0, 9, cardId: "B")),
            true, Effects(typeof(DeVolitionist)));

        Assert.Equal("our BG36_102 4/8 attacks their A 0/5", result.Log[0]);
        Assert.Equal("their A 0/1 takes 4 from our BG36_102 4/8", result.Log[1]);
        Assert.Equal("their B 0/5 takes 4 from our BG36_102 4/8", result.Log[2]);
    }

    [Fact]
    public void Golden_de_volitionist_deals_double_its_attack_and_skips_the_dying_target()
    {
        // Golden 8/16 vs A 0/5, B 0/40. It attacks A (pick 0) and kills it; A is dying, so the highest-Health living enemy
        // is B: it takes 16 (double the Attack) → 0/24.
        var result = Fight(
            Side(Minion(8, 16, cardId: "BG36_102", tribes: Tribe.Aberration, golden: true)),
            Side(Minion(0, 5, cardId: "A"), Minion(0, 40, cardId: "B")),
            true, Effects(typeof(DeVolitionist)));

        Assert.Equal("their B 0/24 takes 16 from our BG36_102(G) 8/16", result.Log[2]);
    }

    // ------------------------------------------------------------------------------------------- Blade Collector

    [Fact]
    public void Blade_collector_also_damages_the_targets_neighbours()
    {
        // Blade Collector 3/2 vs L 0/5, T 1/5, R 0/5; it attacks T (pick 1): T → 1/2 and both neighbours take 3 → 0/2;
        // it takes 1 → 3/1. Their T then attacks it: both die (no cleave when defending). L and R are left at 0/2.
        var result = Fight(
            Side(Minion(3, 2, cardId: "BG26_817", tribes: Tribe.Pirate)),
            Side(Minion(0, 5, cardId: "L"), Minion(1, 5, cardId: "T"), Minion(0, 5, cardId: "R")),
            true, Effects(typeof(BladeCollector)), picks: [1]);

        Assert.Equal(-1, result.Outcome);
        Assert.Equal([(0, 2), (0, 2)], Stats(result.Theirs));
    }

    [Fact]
    public void Golden_blade_collector_cleaves_the_same_way()
    {
        // Golden 6/4 vs L 0/7, T 1/7, R 0/7; attacks T (pick 1): T, L, R → 1 Health each, it → 6/3.
        // T attacks it: it → 6/2, T dies. It attacks L (pick 0): L dies and its neighbour R takes 6 too. Ends 6/2.
        var result = Fight(
            Side(Minion(6, 4, cardId: "BG26_817", tribes: Tribe.Pirate, golden: true)),
            Side(Minion(0, 7, cardId: "L"), Minion(1, 7, cardId: "T"), Minion(0, 7, cardId: "R")),
            true, Effects(typeof(BladeCollector)), picks: [1]);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(6, 2)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Obsidian Ravager

    [Fact]
    public void Obsidian_ravager_rally_hits_the_target_and_one_adjacent_minion_before_attacking()
    {
        // Ravager 7/7 vs L 0/10, T 5/10, R 0/10. Picks: target T (1 of 3), adjacent R (1 of 2).
        // Rally: T → 5/3, R → 0/3. The attack then kills T and the Ravager takes 5 → 7/2.
        // Next it attacks L (pick 0): Rally hits L (→ 0/3) and its only neighbour R (dies); the attack kills L. Ends 7/2.
        var result = Fight(
            Side(Minion(7, 7, cardId: "BG27_017", tribes: Tribe.Dragon)),
            Side(Minion(0, 10, cardId: "L"), Minion(5, 10, cardId: "T"), Minion(0, 10, cardId: "R")),
            true, Effects(typeof(ObsidianRavager)), picks: [1, 1]);

        Assert.Equal(
            ["our BG27_017 7/7 attacks their T 5/10", "their T 5/3 takes 7 from our BG27_017 7/7", "their R 0/3 takes 7 from our BG27_017 7/7"],
            result.Log.Take(3));
        Assert.Equal(1, result.Outcome);
        Assert.Equal([(7, 2)], Stats(result.Ours));
    }

    [Fact]
    public void Obsidian_ravager_attack_is_cancelled_when_the_rally_kills_the_target()
    {
        // Ravager 7/7 vs a 5/7: the Rally's 7 damage kills it before the attack, so the Ravager takes no damage.
        var result = Fight(Side(Minion(7, 7, cardId: "BG27_017", tribes: Tribe.Dragon)), Side(Minion(5, 7, cardId: "T")), true,
                           Effects(typeof(ObsidianRavager)));

        Assert.Equal(1, result.Outcome);
        Assert.Contains("attack cancelled", result.Log);
        Assert.Equal([(7, 7)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_obsidian_ravager_rally_hits_the_target_and_both_neighbours()
    {
        // Golden 14/14 vs L 0/20, T 5/20, R 0/20; target T (pick 1). Rally: T, L and R take 14 each (→ 6 Health).
        var result = Fight(
            Side(Minion(14, 14, cardId: "BG27_017", tribes: Tribe.Dragon, golden: true)),
            Side(Minion(0, 20, cardId: "L"), Minion(5, 20, cardId: "T"), Minion(0, 20, cardId: "R")),
            true, Effects(typeof(ObsidianRavager)), picks: [1]);

        Assert.Equal(
        [
            "our BG27_017(G) 14/14 attacks their T 5/20",
            "their T 5/6 takes 14 from our BG27_017(G) 14/14",
            "their L 0/6 takes 14 from our BG27_017(G) 14/14",
            "their R 0/6 takes 14 from our BG27_017(G) 14/14",
        ], result.Log.Take(4));
    }
}
