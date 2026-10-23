using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects.Deaths;
using static Hearthlens.Tests.Sim.Deaths.DeathsTestKit;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Deaths;

/// <summary>
/// Mummifier and Nadina the Red (deathrattles that give keywords), Leeroy the Reckless (destroy) and Titus Rivendare
/// (deathrattle multiplier). Unless noted, their Killer attacks first and trades with our left-most minion (pick 0).
/// </summary>
public sealed class KeywordDestroyMultiplierTests
{
    private static SimMinion Undead(string id, Keyword keywords = Keyword.None) => Minion(1, 1, keywords, id, Tribe.Undead);

    private static SimMinion Dragon(string id, Keyword keywords = Keyword.None) => Minion(1, 1, keywords, id, Tribe.Dragon);

    // ------------------------------------------------------------------------------------------- Mummifier

    [Fact]
    public void Mummifier_gives_reborn_to_a_different_undead_without_it()
    {
        // Killer 2/5 → Mummifier 5/2 (pick 0 of 5). Candidates: living friendly Undead other than the Mummifier that lack
        // Reborn: U2, U3 (U1 has it, X is not Undead). Pick 1 → U3.
        var result = Fight(
            Side(Minion(5, 2, cardId: Ids.Mummifier, tribes: Tribe.Undead), Undead("U1", Keyword.Reborn), Undead("U2"), Undead("U3"), Minion(1, 1, cardId: "X")),
            Side(Killer(2, 5), Wall), false, Only(typeof(Mummifier)), 0, 1);

        Assert.Equal(["U1 1/1 Reborn", "U2 1/1", "U3 1/1 Reborn", "X 1/1"], Board(result.Ours));
    }

    [Fact]
    public void Golden_mummifier_gives_reborn_to_two_different_undead()
    {
        // Golden text: "Give 2 different friendly Undead Reborn". Picks: 0 (Killer → Mummifier), then 2 of U1, U2, U3:
        // pick 2 → U3, then pick 0 of U1, U2 → U1.
        var result = Fight(
            Side(Minion(10, 4, cardId: Ids.Mummifier, tribes: Tribe.Undead, golden: true), Undead("U1"), Undead("U2"), Undead("U3")),
            Side(Killer(4, 10), Wall), false, Only(typeof(Mummifier)), 0, 2, 0);

        Assert.Equal(["U1 1/1 Reborn", "U2 1/1", "U3 1/1 Reborn"], Board(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Nadina the Red

    [Fact]
    public void Nadina_gives_three_random_dragons_divine_shield()
    {
        // Killer 4/8 → Nadina 8/4 (pick 0 of 6). Three of D1..D4 at random: pick 1 → D2, pick 0 of D1, D3, D4 → D1,
        // pick 1 of D3, D4 → D4. X is not a Dragon.
        var result = Fight(
            Side(Minion(8, 4, cardId: Ids.Nadina), Dragon("D1"), Dragon("D2"), Dragon("D3"), Dragon("D4"), Minion(1, 1, cardId: "X")),
            Side(Killer(4, 8), Wall), false, Only(typeof(NadinaTheRed)), 0, 1, 0, 1);

        Assert.Equal(["D1 1/1 DivineShield", "D2 1/1 DivineShield", "D3 1/1", "D4 1/1 DivineShield", "X 1/1"], Board(result.Ours));
    }

    [Fact]
    public void Nadina_skips_dragons_that_already_have_divine_shield()
    {
        // D1 already has a shield, so the three without one (D2..D4) all get it, without a random pick.
        var result = Fight(
            Side(Minion(8, 4, cardId: Ids.Nadina), Dragon("D1", Keyword.DivineShield), Dragon("D2"), Dragon("D3"), Dragon("D4")),
            Side(Killer(4, 8), Wall), false, Only(typeof(NadinaTheRed)), 0);

        Assert.All(Board(result.Ours), d => Assert.EndsWith("DivineShield", d));
        Assert.Equal(4, result.Ours.Count);
    }

    [Fact]
    public void Golden_nadina_gives_six_dragons_divine_shield()
    {
        // Golden text: "Give 6 friendly Dragons Divine Shield": all five Dragons get it; X does not.
        var result = Fight(
            Side(Minion(16, 8, cardId: Ids.Nadina, golden: true), Dragon("D1"), Dragon("D2"), Dragon("D3"), Dragon("D4"), Dragon("D5"),
                 Minion(1, 1, cardId: "X")),
            Side(Killer(8, 16), Wall), false, Only(typeof(NadinaTheRed)), 0);

        Assert.Equal(["D1 1/1 DivineShield", "D2 1/1 DivineShield", "D3 1/1 DivineShield", "D4 1/1 DivineShield", "D5 1/1 DivineShield", "X 1/1"],
                     Board(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Leeroy the Reckless

    [Fact]
    public void Leeroy_destroys_the_minion_that_killed_it()
    {
        // Their 2/10 kills Leeroy 6/2 and survives at 2/4; Leeroy's deathrattle destroys it. Only their Wall is left.
        var result = Fight(Side(Minion(6, 2, cardId: Ids.Leeroy)), Side(Killer(2, 10), Wall), false, Only(typeof(LeeroyTheReckless)));

        Assert.Contains("their KILLER 2/4 is destroyed", result.Log);
        Assert.Equal(["WALL 0/1000000"], Board(result.Theirs));
    }

    [Fact]
    public void Golden_leeroy_destroys_the_minion_that_killed_it()
    {
        // Golden text is the same. Their 4/20 kills the golden 12/4 Leeroy, survives at 4/8, and is destroyed.
        var result = Fight(Side(Minion(12, 4, cardId: Ids.Leeroy, golden: true)), Side(Killer(4, 20), Wall), false, Only(typeof(LeeroyTheReckless)));

        Assert.Contains("their KILLER 4/8 is destroyed", result.Log);
        Assert.Equal(["WALL 0/1000000"], Board(result.Theirs));
    }

    [Fact]
    public void Leeroy_when_attacking_destroys_the_minion_it_could_not_kill()
    {
        // We attack first: Leeroy 6/2 hits their 2/10 (→ 2/4) and dies to it; the deathrattle destroys it.
        var result = Fight(Side(Minion(6, 2, cardId: Ids.Leeroy), Minion(0, 5, cardId: "Y")), Side(Killer(2, 10), Wall), true,
                           Only(typeof(LeeroyTheReckless)), 0);

        Assert.Contains("their KILLER 2/4 is destroyed", result.Log);
        Assert.Equal(["WALL 0/1000000"], Board(result.Theirs));
    }

    [Fact]
    public void Leeroy_does_nothing_when_its_killer_died_too()
    {
        // Their 2/6 and Leeroy 6/2 trade: the killer is already dead, nothing to destroy.
        var result = Fight(Side(Minion(6, 2, cardId: Ids.Leeroy), Minion(0, 5, cardId: "Y")), Side(Killer(2, 6), Wall), false,
                           Only(typeof(LeeroyTheReckless)), 0);

        Assert.DoesNotContain(result.Log, l => l.EndsWith("is destroyed", StringComparison.Ordinal));
        Assert.Equal(["Y 0/5"], Board(result.Ours));
        Assert.Equal(["WALL 0/1000000"], Board(result.Theirs));
    }

    // ------------------------------------------------------------------------------------------- Titus Rivendare

    [Fact]
    public void Titus_makes_deathrattles_trigger_an_extra_time()
    {
        // Killer → Bonehead (pick 0). Its deathrattle runs twice: four Skeletons, left of Titus.
        var result = Fight(Side(Minion(1, 1, cardId: Ids.Bonehead, tribes: Tribe.Undead), Minion(1, 7, cardId: Ids.Titus)), Side(Killer(1, 1), Wall), false,
                           Only(typeof(HarmlessBonehead), typeof(TitusRivendare)), 0);

        Assert.Equal(Times(4, "BG_ICC_026t 1/1").Append("BG25_354 1/7"), Board(result.Ours));
        Assert.Equal(2, result.Lines("deathrattle of our BG28_300").Count);
    }

    [Fact]
    public void Golden_titus_makes_deathrattles_trigger_two_extra_times()
    {
        // Golden text: "trigger 2 extra times": three triggers, six Skeletons, plus Titus = 7 minions.
        var result = Fight(Side(Minion(1, 1, cardId: Ids.Bonehead, tribes: Tribe.Undead), Minion(2, 14, cardId: Ids.Titus, golden: true)),
                           Side(Killer(1, 1), Wall), false, Only(typeof(HarmlessBonehead), typeof(TitusRivendare)), 0);

        Assert.Equal(Times(6, "BG_ICC_026t 1/1").Append("BG25_354(G) 2/14"), Board(result.Ours));
        Assert.Equal(3, result.Lines("deathrattle of our BG28_300").Count);
    }

    [Fact]
    public void Titus_does_not_multiply_the_opponents_deathrattles()
    {
        // Our Titus 1/7 kills their Bonehead: their deathrattle runs once (two Skeletons).
        var result = Fight(Side(Minion(1, 7, cardId: Ids.Titus)), Side(Minion(1, 1, cardId: Ids.Bonehead, tribes: Tribe.Undead)), true,
                           Only(typeof(HarmlessBonehead), typeof(TitusRivendare)));

        Assert.Single(result.Lines("deathrattle of their BG28_300"));
        Assert.Equal(2, result.Lines("summoned their BG_ICC_026t").Count);
    }

    [Fact]
    public void Titus_multiplies_ghastcoilers_summons()
    {
        // Ghastcoiler + Titus: two triggers of "Summon 2 random Deathrattle minions" → four draws (scripted after the
        // Killer's pick): Bonehead, Cord Puller, Vermin, Scarlet Skull (plain stats: their effects are not in this registry).
        var picks = new[] { 0, Pool(Ids.Bonehead), Pool(Ids.CordPuller), Pool(Ids.Vermin), Pool(Ids.ScarletSkull) };
        var result = Fight(Side(Minion(7, 7, cardId: Ids.Ghastcoiler, tribes: Tribe.Beast), Minion(1, 7, cardId: Ids.Titus)), Side(Killer(7, 7), Wall), false,
                           Only(typeof(Ghastcoiler), typeof(TitusRivendare)), picks);

        Assert.Equal(["BG28_300 1/1", "BG29_611 1/1 DivineShield", "BG31_803 1/1 Taunt", "BG25_022 2/1 Reborn", "BG25_354 1/7"], Board(result.Ours));
    }

    private static int Pool(string cardId)
    {
        var index = GhastcoilerPool.IndexOf(cardId);
        Assert.True(index >= 0, cardId);
        return index;
    }
}
