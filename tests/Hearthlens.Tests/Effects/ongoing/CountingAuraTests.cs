using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects.Ongoing;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Ongoing;

/// <summary>
/// "Has +X/+Y for each ... this game (wherever this is)": Eternal Knight (and its Summoner and Tycoon), San'layn Scribe and
/// Falling Sky Golem. Every test works the expected stats out by hand in its comment. Their enemies are plain minions
/// without effects.
/// </summary>
public sealed class CountingAuraTests
{
    private const string Knight = "BG25_008";
    private const string Scribe = "BGDUO31_208";
    private const string SkyGolem = "BG35_342";

    private static List<(int, int)> Stats(IEnumerable<SimMinion> minions) => minions.Select(m => (m.Attack, m.Health)).ToList();

    private static SimMinion Ek(bool golden = false) =>
        golden ? Minion(8, 4, cardId: Knight, tribes: Tribe.Undead, golden: true) : Minion(4, 2, cardId: Knight, tribes: Tribe.Undead);

    // ------------------------------------------------------------------------------------------- Eternal Knight

    [Fact]
    public void Eternal_knight_gains_four_two_when_another_knight_dies()
    {
        // Their 3/3 attacks our first Knight (pick 0): the 4/2 Knight takes 3 and dies, the 3/3 takes 4 and dies.
        // The surviving Knight gets +4/+2 for the dead one: 4/2 -> 8/4. The captured 4/2 is not counted twice.
        var result = Fight(Side(Ek(), Ek()), Side(Minion(3, 3)), oursFirst: false, Effects(typeof(EternalKnight)), 0);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(8, 4)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_eternal_knight_gains_eight_four()
    {
        // Golden 8/4 Knights. Their 4/8 hits the first one: 4 damage kills it, its 8 Attack kills the 4/8.
        // The other golden Knight has +8/+4 for the dead one: 8/4 -> 16/8.
        var result = Fight(Side(Ek(true), Ek(true)), Side(Minion(4, 8)), oursFirst: false, Effects(typeof(EternalKnight)), 0);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(16, 8)], Stats(result.Ours));
    }

    [Fact]
    public void A_dead_golden_knight_gives_a_normal_knight_four_two_and_a_dead_normal_one_gives_a_golden_knight_eight_four()
    {
        // Each Knight has its own bonus per dead friendly Knight, whichever version died.
        // Golden 8/4 dies to their 4/8; the normal 4/2 becomes 8/4.
        var goldenDies = Fight(Side(Ek(true), Ek()), Side(Minion(4, 8)), oursFirst: false, Effects(typeof(EternalKnight)), 0);
        Assert.Equal([(8, 4)], Stats(goldenDies.Ours));

        // Normal 4/2 dies to their 3/3 (which dies to its 4 Attack); the golden 8/4 becomes 16/8.
        var normalDies = Fight(Side(Ek(), Ek(true)), Side(Minion(3, 3)), oursFirst: false, Effects(typeof(EternalKnight)), 0);
        Assert.Equal([(16, 8)], Stats(normalDies.Ours));
    }

    [Fact]
    public void Eternal_knight_gets_the_bonus_once_for_every_dead_knight()
    {
        // Three 4/2 Knights against their 3/20 (picks all 0).
        // 1) Their 3/20 hits Knight a (dies; they take 4 -> 16). b and c: 4/2 -> 8/4.
        // 2) Our b (8/4) attacks: it takes 3 (8/1), they take 8 -> 8.
        // 3) Their attack on b (pick 0 of [b, c]): b dies, they take 8 -> 0 and die too.
        // c has been told about two dead Knights: 4/2 + 2 x 4/2 = 12/6.
        var result = Fight(Side(Ek(), Ek(), Ek()), Side(Minion(3, 20)), oursFirst: false, Effects(typeof(EternalKnight)), 0, 0, 0);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(12, 6)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Eternal Summoner

    [Fact]
    public void Eternal_summoner_deathrattle_summons_a_knight_and_reborn_brings_the_summoner_back()
    {
        // Our 8/1 Summoner (Reborn) and a 0/20 bystander on its right. Their 1/9 attacks the Summoner (pick 0): it dies
        // (they take 8 -> 1). Deathrattle: a 4/2 Knight (no Knight has died, so no bonus) in its slot, then the reborn 8/1
        // Summoner to its right. The Knight (first in line) attacks and kills the 1-health 1/9 and takes 1: 4/1.
        var summoner = Minion(8, 1, Keyword.Reborn, "BG25_009", Tribe.Undead, mechanics: CardMechanic.Deathrattle);
        var result = Fight(Side(summoner, Minion(0, 20)), Side(Minion(1, 9)), oursFirst: false,
                           Effects(typeof(EternalSummoner), typeof(EternalKnight)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(4, 1), (8, 1), (0, 20)], Stats(result.Ours));
        Assert.Equal([Knight, "BG25_009", string.Empty], result.Ours.Select(m => m.CardId).ToList());
        Assert.Contains("summoned our BG25_008 4/2 at 0", result.Log);
        Assert.Contains("reborn our BG25_009 8/1 at 1", result.Log);
    }

    [Fact]
    public void Golden_eternal_summoner_summons_a_golden_knight()
    {
        // Golden 16/2 Summoner with Reborn and a 0/20 bystander. Their 2/20 kills it (they take 16 -> 4). Deathrattle: a
        // golden 8/4 Knight. The Knight attacks and kills the 2/4 (8 damage) and takes 2: 8/2. The reborn golden Summoner is 16/1.
        var summoner = Minion(16, 2, Keyword.Reborn, "BG25_009", Tribe.Undead, golden: true, mechanics: CardMechanic.Deathrattle);
        var result = Fight(Side(summoner, Minion(0, 20)), Side(Minion(2, 20)), oursFirst: false,
                           Effects(typeof(EternalSummoner), typeof(EternalKnight)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(8, 2), (16, 1), (0, 20)], Stats(result.Ours));
        Assert.True(result.Ours[0].Golden);
    }

    [Fact]
    public void A_knight_summoned_in_combat_counts_the_knights_that_died_before_and_during_the_fight()
    {
        // The summoner has no Reborn here. Our 4/2 Knight and our 8/1 Summoner against their 3/12.
        // 1) They hit the Knight (pick 0): it dies (they take 4 -> 8).
        // 2) Our Summoner (8/1) attacks: it dies to 3 damage, they take 8 -> 0 and die. Its deathrattle summons a Knight
        //    that knows about the dead Knight: 4/2 + 4/2 = 8/4.
        var summoner = Minion(8, 1, cardId: "BG25_009", tribes: Tribe.Undead, mechanics: CardMechanic.Deathrattle);
        var result = Fight(Side(Ek(), summoner), Side(Minion(3, 12)), oursFirst: false, Effects(typeof(EternalSummoner), typeof(EternalKnight)), 0);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(8, 4)], Stats(result.Ours));
    }

    [Fact]
    public void A_knight_summoned_in_combat_counts_knights_that_died_before_the_fight_from_the_captured_copy()
    {
        // The captured Knight is 8/4: 4/2 plus one dead Knight earlier in the game. A Knight summoned now (their 2/9 kills the
        // 1-health Summoner first) starts at 4/2 and takes the same +4/+2: 8/4. The captured Knight stays 8/4.
        var oldKnight = Ek() with { Attack = 8, Health = 4, MaxHealth = 4 };
        var summoner = Minion(0, 1, cardId: "BG25_009", tribes: Tribe.Undead, mechanics: CardMechanic.Deathrattle);
        var result = Fight(Side(oldKnight, summoner), Side(Minion(2, 1)), oursFirst: false, Effects(typeof(EternalSummoner), typeof(EternalKnight)), 1);

        // They attack the Summoner (pick 1): it dies, the deathrattle summons the Knight; our 8/4 Knight then kills their 2/1.
        Assert.Equal(1, result.Outcome);
        Assert.Equal([(8, 2), (8, 4)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Eternal Tycoon

    [Fact]
    public void Eternal_tycoon_summons_a_knight_that_attacks_immediately_after_five_friendly_deaths()
    {
        // Our Tycoon (0/30 for this test) and five 0/1 minions that never attack, against their 1/3 (picks: 1 = the minion
        // right of the Tycoon every time). Their 1/3 kills one minion per attack. After the fifth death: Avenge (5) summons
        // a 4/2 Knight right of the Tycoon, and it attacks immediately: it kills the 1/3 (4 damage) and takes 1: 4/1.
        var tycoon = Minion(0, 30, cardId: "BG34_403", tribes: Tribe.Undead, mechanics: CardMechanic.Avenge);
        var ours = Side(tycoon, Minion(0, 1), Minion(0, 1), Minion(0, 1), Minion(0, 1), Minion(0, 1));
        var result = Fight(ours, Side(Minion(1, 3)), oursFirst: false, Effects(typeof(EternalTycoon), typeof(EternalKnight)), 1, 1, 1, 1, 1);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(0, 30), (4, 1)], Stats(result.Ours));
        var summoned = result.IndexOf("summoned our BG25_008 4/2 at 1");
        Assert.True(summoned > 0);
        Assert.StartsWith("our BG25_008 4/2 attacks their", result.Log[summoned + 1], StringComparison.Ordinal);
    }

    [Fact]
    public void Golden_eternal_tycoon_summons_a_golden_knight()
    {
        // As above with a golden Tycoon: the golden 8/4 Knight attacks immediately, kills the 1/3 and takes 1: 8/3.
        var tycoon = Minion(0, 30, cardId: "BG34_403", tribes: Tribe.Undead, golden: true, mechanics: CardMechanic.Avenge);
        var ours = Side(tycoon, Minion(0, 1), Minion(0, 1), Minion(0, 1), Minion(0, 1), Minion(0, 1));
        var result = Fight(ours, Side(Minion(1, 3)), oursFirst: false, Effects(typeof(EternalTycoon), typeof(EternalKnight)), 1, 1, 1, 1, 1);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(0, 30), (8, 3)], Stats(result.Ours));
        Assert.True(result.Ours[1].Golden);
    }

    [Fact]
    public void Eternal_tycoon_does_nothing_before_the_fifth_death()
    {
        // Four deaths only: no Knight.
        var tycoon = Minion(0, 30, cardId: "BG34_403", tribes: Tribe.Undead, mechanics: CardMechanic.Avenge);
        var ours = Side(tycoon, Minion(0, 1), Minion(0, 1), Minion(0, 1), Minion(0, 1), Minion(0, 20));
        var result = Fight(ours, Side(Minion(1, 3)), oursFirst: false, Effects(typeof(EternalTycoon), typeof(EternalKnight)), 1, 1, 1, 1, 4);

        Assert.Empty(result.Lines("summoned"));
    }

    [Fact]
    public void Tycoon_knight_counts_a_knight_that_died_in_the_same_fight()
    {
        // Our 4/2 Knight (0 Attack for the test) is the first death; four 0/1 minions follow: Avenge (5) fires after the fifth
        // death. Their 2/3 kills one per attack (picks: 0 = the Knight, then 1 = the minion right of the Tycoon).
        // The new Knight is summoned as 4/2 and at once has +4/+2 for the one that died: it attacks as an 8/4, kills their
        // 2/3 (8 damage) and takes 2: 8/2.
        var tycoon = Minion(0, 30, cardId: "BG34_403", tribes: Tribe.Undead, mechanics: CardMechanic.Avenge);
        var oldKnight = Ek() with { Attack = 0 };
        var ours = Side(tycoon, oldKnight, Minion(0, 1), Minion(0, 1), Minion(0, 1), Minion(0, 1));
        var result = Fight(ours, Side(Minion(2, 3)), oursFirst: false, Effects(typeof(EternalTycoon), typeof(EternalKnight)), 1, 1, 1, 1, 1);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(0, 30), (8, 2)], Stats(result.Ours));
        var summoned = result.IndexOf("summoned our BG25_008 4/2 at 1");
        Assert.True(summoned > 0);
        Assert.StartsWith("our BG25_008 8/4 attacks their", result.Log[summoned + 1], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------- San'layn Scribe

    [Fact]
    public void Sanlayn_scribe_gains_four_four_when_another_scribe_dies()
    {
        // Their 4/4 kills the first 4/4 Scribe (and dies to its 4 Attack). The other has +4/+4: 8/8.
        SimMinion Sc(bool golden = false) => golden
            ? Minion(8, 8, cardId: Scribe, tribes: Tribe.Undead, golden: true)
            : Minion(4, 4, cardId: Scribe, tribes: Tribe.Undead);

        var normal = Fight(Side(Sc(), Sc()), Side(Minion(4, 4)), oursFirst: false, Effects(typeof(SanlaynScribe)), 0);
        Assert.Equal(1, normal.Outcome);
        Assert.Equal([(8, 8)], Stats(normal.Ours));

        // Golden 8/8 Scribes, their 8/8: the other golden Scribe has +8/+8: 16/16.
        var golden = Fight(Side(Sc(true), Sc(true)), Side(Minion(8, 8)), oursFirst: false, Effects(typeof(SanlaynScribe)), 0);
        Assert.Equal(1, golden.Outcome);
        Assert.Equal([(16, 16)], Stats(golden.Ours));
    }

    [Fact]
    public void Sanlayn_scribe_does_not_react_to_knights_dying()
    {
        // A dead Eternal Knight is not a dead Scribe.
        var result = Fight(Side(Ek(), Minion(4, 4, cardId: Scribe, tribes: Tribe.Undead)), Side(Minion(3, 3)), oursFirst: false,
                           Effects(typeof(SanlaynScribe), typeof(EternalKnight)), 0);

        Assert.Equal([(4, 4)], Stats(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Falling Sky Golem

    private static SimMinion Sky(bool golden = false) =>
        golden
            ? Minion(8, 4, Keyword.DivineShield, SkyGolem, Tribe.Mech, golden: true)
            : Minion(4, 2, Keyword.DivineShield, SkyGolem, Tribe.Mech);

    private static SimMinion DeathrattleMinion() => Minion(1, 1, cardId: "TEST_DR_NOTHING", mechanics: CardMechanic.Deathrattle);

    [Fact]
    public void Falling_sky_golem_gains_four_two_for_a_deathrattle_trigger()
    {
        // Their 2/3 kills our 1/1 deathrattle minion (pick 1): one trigger, so the Golem is 8/4 (and the 2/3 takes 1 -> 2/2).
        // Our 8/4 Golem attacks the 2/2: it kills it, and its Divine Shield absorbs the 2 damage (and is lost).
        var result = Fight(Side(Sky(), DeathrattleMinion()), Side(Minion(2, 3)), oursFirst: false,
                           Effects(typeof(FallingSkyGolem), typeof(DrNothing)), 1);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(8, 4)], Stats(result.Ours));
        Assert.False(result.Ours[0].DivineShield);
    }

    [Fact]
    public void Falling_sky_golem_counts_every_trigger_including_extra_ones()
    {
        // With a Titus-style minion (0/9, no attack) the deathrattle triggers twice: +8/+4 -> 12/6.
        var titus = Minion(0, 9, cardId: "TEST_TITUS");
        var result = Fight(Side(Sky(), DeathrattleMinion(), titus), Side(Minion(2, 3)), oursFirst: false,
                           Effects(typeof(FallingSkyGolem), typeof(DrNothing), typeof(TestTitus)), 1);

        Assert.Equal(1, result.Outcome);
        Assert.Equal(12, result.Ours[0].Attack);
        Assert.Equal(6, result.Ours[0].Health);
    }

    [Fact]
    public void Golden_falling_sky_golem_gains_eight_four_per_trigger()
    {
        // Golden 8/4 Golem, one trigger: 16/8.
        var result = Fight(Side(Sky(true), DeathrattleMinion()), Side(Minion(2, 3)), oursFirst: false,
                           Effects(typeof(FallingSkyGolem), typeof(DrNothing)), 1);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(16, 8)], Stats(result.Ours));
    }

    [Fact]
    public void A_falling_sky_golem_summoned_by_a_deathrattle_counts_that_trigger()
    {
        // The deathrattle minion summons a 4/2 Golem. "Deathrattles you've triggered" includes the one that created it,
        // so the new Golem ends the trigger at 8/4. Their 2/3 attacks our minion (only one, pick 0): both die.
        var summoner = Minion(1, 1, cardId: "TEST_DR_GOLEM", mechanics: CardMechanic.Deathrattle);
        var result = Fight(Side(summoner), Side(Minion(1, 3)), oursFirst: false, Effects(typeof(FallingSkyGolem), typeof(DrSummonFallingSkyGolem)));

        Assert.Equal([(8, 4)], Stats(result.Ours));
    }
}
