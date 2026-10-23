using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects.Ongoing;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Ongoing;

/// <summary>Reborn and damage triggers: Barrier Banshee, Snazzy Phantom and Devout Hellcaller.</summary>
public sealed class RebornAndDamageTests
{
    private static List<(int, int)> Stats(IEnumerable<SimMinion> minions) => minions.Select(m => (m.Attack, m.Health)).ToList();

    // ------------------------------------------------------------------------------------------- Barrier Banshee

    [Fact]
    public void Barrier_banshee_gains_divine_shield_and_eight_eight_after_a_friendly_minion_is_reborn()
    {
        // Banshee 8/8 and a 3/1 Beast with Reborn. Their poisonous 1/3 attacks the Beast (pick 1): the Beast dies (and they
        // take 3 -> dead). The Beast comes back as a 3/1 (no Reborn), and the Banshee gains Divine Shield and +8/+8: 16/16.
        var ours = Side(Minion(8, 8, cardId: "BG36_514", tribes: Tribe.Undead), Minion(3, 1, Keyword.Reborn, "RB", Tribe.Beast));
        var result = Fight(ours, Side(Minion(1, 3, Keyword.Poisonous)), oursFirst: false, Effects(typeof(BarrierBanshee)), 1);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(16, 16), (3, 1)], Stats(result.Ours));
        Assert.True(result.Ours[0].DivineShield);
        Assert.False(result.Ours[1].Reborn);
    }

    [Fact]
    public void Golden_barrier_banshee_gains_sixteen_sixteen()
    {
        // Golden Banshee 16/16 -> 32/32 with Divine Shield.
        var ours = Side(Minion(16, 16, cardId: "BG36_514", tribes: Tribe.Undead, golden: true), Minion(3, 1, Keyword.Reborn, "RB", Tribe.Beast));
        var result = Fight(ours, Side(Minion(1, 3, Keyword.Poisonous)), oursFirst: false, Effects(typeof(BarrierBanshee)), 1);

        Assert.Equal([(32, 32), (3, 1)], Stats(result.Ours));
        Assert.True(result.Ours[0].DivineShield);
    }

    [Fact]
    public void Barrier_banshee_ignores_deaths_without_reborn()
    {
        // The 3/1 minion has no Reborn: it just dies (they take 3 -> dead). The Banshee stays 8/8.
        var ours = Side(Minion(8, 8, cardId: "BG36_514", tribes: Tribe.Undead), Minion(3, 1, Keyword.None, "RB", Tribe.Beast));
        var result = Fight(ours, Side(Minion(1, 3, Keyword.Poisonous)), oursFirst: false, Effects(typeof(BarrierBanshee)), 1);

        Assert.Equal([(8, 8)], Stats(result.Ours));
        Assert.False(result.Ours[0].DivineShield);
    }

    // ------------------------------------------------------------------------------------------- Snazzy Phantom

    [Fact]
    public void Snazzy_phantom_gives_the_reborn_minions_attack_as_stats_to_the_right_most_undead()
    {
        // Phantom 6/8 (Undead), a 3/1 Beast with Reborn and a 2/5 Undead on the right. Their poisonous 1/3 kills the Beast
        // (pick 1; the Beast kills it in return). The Beast comes back as a 3/1: +3/+3 to the right-most Undead, the 2/5:
        // 5/8. The Phantom stays 6/8.
        var ours = Side(
            Minion(6, 8, cardId: "BG36_515", tribes: Tribe.Undead),
            Minion(3, 1, Keyword.Reborn, "RB", Tribe.Beast),
            Minion(2, 5, cardId: "U", tribes: Tribe.Undead));
        var result = Fight(ours, Side(Minion(1, 3, Keyword.Poisonous)), oursFirst: false, Effects(typeof(SnazzyPhantom)), 1);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(6, 8), (3, 1), (5, 8)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_snazzy_phantom_gives_double_the_attack()
    {
        // Golden Phantom: double the Beast's Attack: +6/+6 to the 2/5 Undead: 8/11.
        var ours = Side(
            Minion(12, 16, cardId: "BG36_515", tribes: Tribe.Undead, golden: true),
            Minion(3, 1, Keyword.Reborn, "RB", Tribe.Beast),
            Minion(2, 5, cardId: "U", tribes: Tribe.Undead));
        var result = Fight(ours, Side(Minion(1, 3, Keyword.Poisonous)), oursFirst: false, Effects(typeof(SnazzyPhantom)), 1);

        Assert.Equal([(12, 16), (3, 1), (8, 11)], Stats(result.Ours));
    }

    [Fact]
    public void Snazzy_phantom_can_pick_the_reborn_minion_itself_when_it_is_the_right_most_undead()
    {
        // The reborn minion is an Undead on the right of the Phantom: it is the right-most Undead and gets its own Attack
        // as stats: a 3/1 comes back as 6/4.
        var ours = Side(
            Minion(6, 8, cardId: "BG36_515", tribes: Tribe.Undead),
            Minion(3, 1, Keyword.Reborn, "RB", Tribe.Undead));
        var result = Fight(ours, Side(Minion(1, 3, Keyword.Poisonous)), oursFirst: false, Effects(typeof(SnazzyPhantom)), 1);

        Assert.Equal([(6, 8), (6, 4)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Devout Hellcaller

    [Fact]
    public void Devout_hellcaller_gains_two_two_after_another_demon_deals_damage()
    {
        // Hellcaller 4/4 and a 2/5 Demon against their 0/10 (it never attacks), ours first.
        // 1) The Hellcaller attacks: 4 damage (10 -> 6); its own damage does not count ("another").
        // 2) The Demon attacks: 2 damage (6 -> 4). The Hellcaller gains +2/+2: 6/6.
        // 3) The Hellcaller attacks again: 6 damage kills the 4-health minion. The Demon is untouched.
        var ours = Side(Minion(4, 4, cardId: "BG33_155", tribes: Tribe.Demon), Minion(2, 5, cardId: "D", tribes: Tribe.Demon));
        var result = Fight(ours, Side(Minion(0, 10)), oursFirst: true, Effects(typeof(DevoutHellcaller)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(6, 6), (2, 5)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_devout_hellcaller_gains_four_four()
    {
        // Golden Hellcaller 8/8, Demon 2/5, their 0/20: 8 (20 -> 12), Demon 2 (12 -> 10) and the Hellcaller grows to 12/12,
        // then 12 damage kills the 10-health minion.
        var ours = Side(Minion(8, 8, cardId: "BG33_155", tribes: Tribe.Demon, golden: true), Minion(2, 5, cardId: "D", tribes: Tribe.Demon));
        var result = Fight(ours, Side(Minion(0, 20)), oursFirst: true, Effects(typeof(DevoutHellcaller)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(12, 12), (2, 5)], Stats(result.Ours));
    }

    [Fact]
    public void Devout_hellcaller_ignores_minions_that_are_not_demons()
    {
        // Same fight with a Beast instead of the Demon: the Hellcaller stays 4/4 (4 + 2 + 4 = 10 damage kills the 0/10).
        var ours = Side(Minion(4, 4, cardId: "BG33_155", tribes: Tribe.Demon), Minion(2, 5, cardId: "B", tribes: Tribe.Beast));
        var result = Fight(ours, Side(Minion(0, 10)), oursFirst: true, Effects(typeof(DevoutHellcaller)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(4, 4), (2, 5)], Stats(result.Ours));
    }

    [Fact]
    public void Devout_hellcaller_does_not_react_to_a_hit_that_only_broke_a_divine_shield()
    {
        // Their 0/1 has Divine Shield, ours first. The Demon (2/5, first in line) attacks: the shield absorbs it, no damage
        // is dealt, so the Hellcaller stays 4/4. Then the Hellcaller attacks and kills the 1-health minion (its own damage
        // does not count).
        var ours = Side(Minion(2, 5, cardId: "D", tribes: Tribe.Demon), Minion(4, 4, cardId: "BG33_155", tribes: Tribe.Demon));
        var result = Fight(ours, Side(Minion(0, 1, Keyword.DivineShield)), oursFirst: true, Effects(typeof(DevoutHellcaller)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(2, 5), (4, 4)], Stats(result.Ours));
    }
}
