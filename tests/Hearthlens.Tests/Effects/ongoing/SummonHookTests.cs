using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects.Ongoing;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Ongoing;

/// <summary>
/// Summon triggers: Banana Slamma, Lurking Leviathan, Stalwart Kodo and Humming Bird. The summons come from small test
/// deathrattles (2/3 Beasts) on our side; their 1/1 or 1/2 kills the deathrattle minion with its one attack.
/// </summary>
public sealed class SummonHookTests
{
    private static List<(int, int)> Stats(IEnumerable<SimMinion> minions) => minions.Select(m => (m.Attack, m.Health)).ToList();

    private static SimMinion Summoner(string effect, int health = 1) => Minion(1, health, cardId: effect, mechanics: CardMechanic.Deathrattle);

    // ------------------------------------------------------------------------------------------- Banana Slamma

    [Fact]
    public void Banana_slamma_doubles_the_attack_of_a_summoned_beast_and_only_beasts()
    {
        // Slamma 3/6, a 1/1 deathrattle minion (summons a 2/3 Beast, then a 2/3 Mech) and a 0/9 bystander on its right.
        // Their 1/1 attacks the deathrattle minion (pick 1): both die. The Beast comes in at 2/3 and Slamma doubles its Attack
        // (4/3); the Mech is not a Beast and stays 2/3.
        var ours = Side(Minion(3, 6, cardId: "BG26_802", tribes: Tribe.Beast), Summoner("TEST_DR_BEAST_MECH"), Minion(0, 9));
        var result = Fight(ours, Side(Minion(1, 1)), oursFirst: false,
                           Effects(typeof(BananaSlamma), typeof(DrSummonBeastAndMech)), 1);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(3, 6), (4, 3), (2, 3), (0, 9)], Stats(result.Ours));
        Assert.Equal([Ids.BeastToken, Ids.MechToken], result.Ours.Skip(1).Take(2).Select(m => m.CardId).ToList());
    }

    [Fact]
    public void Golden_banana_slamma_triples_the_attack()
    {
        // Golden Slamma 6/12: the 2/3 Beast has its Attack tripled: 6/3.
        var ours = Side(Minion(6, 12, cardId: "BG26_802", tribes: Tribe.Beast, golden: true), Summoner("TEST_DR_BEAST"), Minion(0, 9));
        var result = Fight(ours, Side(Minion(1, 1)), oursFirst: false, Effects(typeof(BananaSlamma), typeof(DrSummonBeast)), 1);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(6, 12), (6, 3), (0, 9)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Lurking Leviathan

    [Fact]
    public void Lurking_leviathan_gives_each_summoned_beast_more_attack_and_improves_each_time()
    {
        // Leviathan 3/9 with a current bonus of +3 (captured TAG_SCRIPT_DATA_NUM_2). The deathrattle minion summons two
        // 2/3 Beasts left to right. The first gets +3 Attack: 5/3; the Leviathan improves (+1, assumed) to +4, so the second
        // gets +4: 6/3.
        var leviathan = Minion(3, 9, cardId: "BG35_602", tribes: Tribe.Beast) with { ScriptData2 = 3 };
        var ours = Side(leviathan, Summoner("TEST_DR_TWO_BEASTS"), Minion(0, 9));
        var result = Fight(ours, Side(Minion(1, 1)), oursFirst: false, Effects(typeof(LurkingLeviathan), typeof(DrSummonTwoBeasts)), 1);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(3, 9), (5, 3), (6, 3), (0, 9)], Stats(result.Ours));
    }

    [Fact]
    public void Lurking_leviathan_starts_at_its_printed_bonus_without_captured_data()
    {
        // No script data: +3 for the first Beast (5/3), then +4 (6/3).
        var ours = Side(Minion(3, 9, cardId: "BG35_602", tribes: Tribe.Beast), Summoner("TEST_DR_TWO_BEASTS"), Minion(0, 9));
        var result = Fight(ours, Side(Minion(1, 1)), oursFirst: false, Effects(typeof(LurkingLeviathan), typeof(DrSummonTwoBeasts)), 1);

        Assert.Equal([(3, 9), (5, 3), (6, 3), (0, 9)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_lurking_leviathan_gives_six_attack_and_improves_by_two()
    {
        // Golden Leviathan 6/18 with a bonus of +6: first Beast 2/3 -> 8/3, improved to +8, second -> 10/3.
        var leviathan = Minion(6, 18, cardId: "BG35_602", tribes: Tribe.Beast, golden: true) with { ScriptData2 = 6 };
        var ours = Side(leviathan, Summoner("TEST_DR_TWO_BEASTS"), Minion(0, 9));
        var result = Fight(ours, Side(Minion(1, 1)), oursFirst: false, Effects(typeof(LurkingLeviathan), typeof(DrSummonTwoBeasts)), 1);

        Assert.Equal([(6, 18), (8, 3), (10, 3), (0, 9)], Stats(result.Ours));
    }

    [Fact]
    public void Lurking_leviathan_buff_comes_before_banana_slammas_doubling()
    {
        // "Whenever you summon" resolves before "after you summon": the Beast is 2/3, +3 Attack (5/3) from the Leviathan,
        // then Slamma doubles that: 10/3.
        var ours = Side(
            Minion(3, 9, cardId: "BG35_602", tribes: Tribe.Beast),
            Minion(3, 6, cardId: "BG26_802", tribes: Tribe.Beast),
            Summoner("TEST_DR_BEAST"),
            Minion(0, 9));
        var result = Fight(ours, Side(Minion(1, 1)), oursFirst: false,
                           Effects(typeof(LurkingLeviathan), typeof(BananaSlamma), typeof(DrSummonBeast)), 2);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(3, 9), (3, 6), (10, 3), (0, 9)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Stalwart Kodo

    [Fact]
    public void Stalwart_kodo_gives_its_maximum_stats_to_the_first_three_summoned_minions()
    {
        // Kodo 3/10 (for the test) with 3 uses (captured TAG_SCRIPT_DATA_NUM_1 = 3). The deathrattle minion summons four 2/3
        // Beasts. The first three get +3/+10: 5/13 each; the fourth is not buffed: 2/3.
        var kodo = Minion(3, 10, cardId: "BG34_322", tribes: Tribe.Beast) with { ScriptData1 = 3 };
        var ours = Side(kodo, Summoner("TEST_DR_FOUR_BEASTS"), Minion(0, 9));
        var result = Fight(ours, Side(Minion(1, 1)), oursFirst: false, Effects(typeof(StalwartKodo), typeof(DrSummonFourBeasts)), 1);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(3, 10), (5, 13), (5, 13), (5, 13), (2, 3), (0, 9)], Stats(result.Ours));
    }

    [Fact]
    public void Stalwart_kodo_without_script_data_has_three_uses()
    {
        var ours = Side(Minion(3, 10, cardId: "BG34_322", tribes: Tribe.Beast), Summoner("TEST_DR_FOUR_BEASTS"), Minion(0, 9));
        var result = Fight(ours, Side(Minion(1, 1)), oursFirst: false, Effects(typeof(StalwartKodo), typeof(DrSummonFourBeasts)), 1);

        Assert.Equal([(3, 10), (5, 13), (5, 13), (5, 13), (2, 3), (0, 9)], Stats(result.Ours));
    }

    [Fact]
    public void Stalwart_kodo_keeps_counting_the_uses_left_from_the_capture()
    {
        // One use left ("1 left!"): only the first Beast is buffed.
        var kodo = Minion(3, 10, cardId: "BG34_322", tribes: Tribe.Beast) with { ScriptData1 = 1 };
        var ours = Side(kodo, Summoner("TEST_DR_TWO_BEASTS"), Minion(0, 9));
        var result = Fight(ours, Side(Minion(1, 1)), oursFirst: false, Effects(typeof(StalwartKodo), typeof(DrSummonTwoBeasts)), 1);

        Assert.Equal([(3, 10), (5, 13), (2, 3), (0, 9)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_stalwart_kodo_gives_double_its_maximum_stats()
    {
        // Golden Kodo 6/20: double its maximum stats = +12/+40 for three summons: 2/3 -> 14/43.
        var kodo = Minion(6, 20, cardId: "BG34_322", tribes: Tribe.Beast, golden: true) with { ScriptData1 = 3 };
        var ours = Side(kodo, Summoner("TEST_DR_FOUR_BEASTS"), Minion(0, 9));
        var result = Fight(ours, Side(Minion(1, 1)), oursFirst: false, Effects(typeof(StalwartKodo), typeof(DrSummonFourBeasts)), 1);

        Assert.Equal([(6, 20), (14, 43), (14, 43), (14, 43), (2, 3), (0, 9)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Humming Bird

    [Fact]
    public void Humming_bird_gives_beasts_summoned_later_one_attack()
    {
        // The Start of Combat part is in the captured stats. A Beast summoned later gets +1 Attack for the rest of the
        // combat: 2/3 -> 3/3. The Mech is not a Beast: 2/3.
        var ours = Side(Minion(1, 4, cardId: "BG26_805", tribes: Tribe.Beast), Summoner("TEST_DR_BEAST_MECH"), Minion(0, 9));
        var result = Fight(ours, Side(Minion(1, 1)), oursFirst: false, Effects(typeof(HummingBird), typeof(DrSummonBeastAndMech)), 1);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(1, 4), (3, 3), (2, 3), (0, 9)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_humming_bird_gives_two_attack()
    {
        var ours = Side(Minion(2, 8, cardId: "BG26_805", tribes: Tribe.Beast, golden: true), Summoner("TEST_DR_BEAST"), Minion(0, 9));
        var result = Fight(ours, Side(Minion(1, 1)), oursFirst: false, Effects(typeof(HummingBird), typeof(DrSummonBeast)), 1);

        Assert.Equal([(2, 8), (4, 3), (0, 9)], Stats(result.Ours));
    }

    [Fact]
    public void Humming_bird_buff_lasts_after_the_bird_has_died()
    {
        // Their 4/30 kills the Bird first (pick 0), then the deathrattle minion (pick 0 again: nothing else can attack, the
        // rest has no Attack). The Beast it summons is still +1: 3/3.
        var ours = Side(Minion(0, 4, cardId: "BG26_805", tribes: Tribe.Beast), Summoner("TEST_DR_BEAST") with { Attack = 0 }, Minion(0, 40));
        var result = Fight(ours, Side(Minion(4, 30)), oursFirst: false, Effects(typeof(HummingBird), typeof(DrSummonBeast)), 0, 0);

        Assert.Contains(result.Log, l => l.StartsWith("summoned our TEST_BEAST 3/3", StringComparison.Ordinal));
    }
}
