using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects.Deaths;
using static Hearthlens.Tests.Sim.Deaths.DeathsTestKit;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Deaths;

/// <summary>
/// Deathrattles that buff: Scarlet Skull, Tasty Lobster, Plaguerunner, Goldrinn, Ship Master Eudora, Showy Cyclist.
/// Unless noted, their Killer attacks first and trades with our minion (pick 0 = our left-most minion), then only their
/// 0-Attack Wall is left, so the survivors are the board right after the deathrattle.
/// </summary>
public sealed class BuffDeathrattleTests
{
    // ------------------------------------------------------------------------------------------- Scarlet Skull

    [Fact]
    public void Scarlet_skull_buffs_a_friendly_undead_then_comes_back()
    {
        // Killer 1/1 → Skull 2/1 (pick 0 of 3); both die. Deathrattle: the only friendly Undead U 3/3 → 4/5 (its pick
        // is scripted 0). Then Reborn: a fresh 2/1 Skull without Reborn in its slot.
        var result = Fight(
            Side(Minion(2, 1, Keyword.Reborn, Ids.ScarletSkull, Tribe.Undead), Minion(3, 3, cardId: "U", tribes: Tribe.Undead), Minion(5, 5, cardId: "X")),
            Side(Killer(1, 1), Wall), false, Only(typeof(ScarletSkull)), 0, 0);

        Assert.Equal(["BG25_022 2/1", "U 4/5", "X 5/5"], Board(result.Ours));
    }

    [Fact]
    public void Golden_scarlet_skull_gives_two_four()
    {
        // Golden text: "+2/+4". U 3/3 → 5/7; the golden Skull comes back as a golden 4/1.
        var result = Fight(
            Side(Minion(4, 2, Keyword.Reborn, Ids.ScarletSkull, Tribe.Undead, golden: true), Minion(3, 3, cardId: "U", tribes: Tribe.Undead)),
            Side(Killer(2, 2), Wall), false, Only(typeof(ScarletSkull)), 0, 0);

        Assert.Equal(["BG25_022(G) 4/1", "U 5/7"], Board(result.Ours));
    }

    [Fact]
    public void Scarlet_skull_deathrattle_runs_again_after_reborn()
    {
        // 1. Their K1 1/1 → Skull (pick 0): both die; U 3/3 → 4/5; the Skull comes back 2/1 in slot 0.
        // 2. Our reborn Skull attacks K2 1/1 (pick 0): both die; deathrattle again: U → 5/7. No second Reborn.
        var result = Fight(
            Side(Minion(2, 1, Keyword.Reborn, Ids.ScarletSkull, Tribe.Undead), Minion(3, 3, cardId: "U", tribes: Tribe.Undead)),
            Side(Killer(1, 1), Killer(1, 1), Wall), false, Only(typeof(ScarletSkull)));

        Assert.Equal(["U 5/7"], Board(result.Ours));
        Assert.Equal(2, result.Lines("deathrattle of our BG25_022").Count);
    }

    // ------------------------------------------------------------------------------------------- Tasty Lobster

    [Fact]
    public void Lobster_gives_a_random_friendly_beast_the_bonus_its_text_shows()
    {
        // Killer → Lobster (pick 0 of 4). Deathrattle: a random friendly Beast among B1, B2 (pick 1 → B2) gets +2/+1
        // (script data 2/1): B2 3/3 → 5/4. X is not a Beast.
        var lobster = Minion(2, 1, cardId: Ids.Lobster, tribes: Tribe.Beast) with { ScriptData1 = 2, ScriptData2 = 1 };
        var result = Fight(
            Side(lobster, Minion(1, 1, cardId: "B1", tribes: Tribe.Beast), Minion(3, 3, cardId: "B2", tribes: Tribe.Beast), Minion(1, 1, cardId: "X")),
            Side(Killer(1, 1), Wall), false, Only(typeof(TastyLobster)), 0, 1);

        Assert.Equal(["B1 1/1", "B2 5/4", "X 1/1"], Board(result.Ours));
    }

    [Fact]
    public void Improved_lobster_uses_its_script_data()
    {
        // "Improve your future Tasty Lobsters": this one shows +6/+3. B 1/1 → 7/4.
        var lobster = Minion(2, 1, cardId: Ids.Lobster, tribes: Tribe.Beast) with { ScriptData1 = 6, ScriptData2 = 3 };
        var result = Fight(Side(lobster, Minion(1, 1, cardId: "B", tribes: Tribe.Beast)), Side(Killer(1, 1), Wall), false, Only(typeof(TastyLobster)), 0);

        Assert.Equal(["B 7/4"], Board(result.Ours));
    }

    [Fact]
    public void Golden_lobster_gives_four_two()
    {
        // Golden text: "+4/+2" (no script data, no other Lobster: the golden card's printed numbers). B 1/1 → 5/3.
        var result = Fight(Side(Minion(4, 2, cardId: Ids.Lobster, tribes: Tribe.Beast, golden: true), Minion(1, 1, cardId: "B", tribes: Tribe.Beast)),
                           Side(Killer(2, 4), Wall), false, Only(typeof(TastyLobster)), 0);

        Assert.Equal(["B 5/3"], Board(result.Ours));
    }

    [Fact]
    public void Lobster_without_script_data_uses_the_best_captured_lobster()
    {
        // Killer → Lobster A (pick 0). A has no script data (like one made in combat); Lobster B shows +6/+3, the
        // player's current value. The Beast pick (1) is C among B, C: C 1/1 → 7/4.
        var improved = Minion(2, 1, cardId: Ids.Lobster, tribes: Tribe.Beast) with { ScriptData1 = 6, ScriptData2 = 3 };
        var result = Fight(Side(Minion(2, 1, cardId: Ids.Lobster, tribes: Tribe.Beast), improved, Minion(1, 1, cardId: "C", tribes: Tribe.Beast)),
                           Side(Killer(1, 1), Wall), false, Only(typeof(TastyLobster)), 0, 1);

        Assert.Equal(["BG36_202 2/1", "C 7/4"], Board(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Plaguerunner

    [Fact]
    public void Plaguerunner_gives_undead_attack_now_and_to_undead_summoned_later()
    {
        // 1. Their K1 2/4 → Plaguerunner (pick 0): both die. Undead +2 Attack: U 1/1 Reborn → 3/1. X (Beast) unchanged.
        // 2. Our U attacks K2 1/1 (pick 0): both die. U's reborn copy (printed 1/1) gets the +2 too: 3/1.
        var result = Fight(
            Side(Minion(4, 2, cardId: Ids.Plaguerunner, tribes: Tribe.Undead), Minion(1, 1, Keyword.Reborn, "U", Tribe.Undead),
                 Minion(2, 2, cardId: "X", tribes: Tribe.Beast)),
            Side(Killer(2, 4), Killer(1, 1), Wall), false, Only(typeof(Plaguerunner)));

        Assert.Equal(["U 3/1", "X 2/2"], Board(result.Ours));
        Assert.Contains("reborn our U 3/1 at 0", result.Log);
    }

    [Fact]
    public void Golden_plaguerunner_gives_four_attack()
    {
        // Golden text: "+4 Attack". U 1/1 → 5/1.
        var result = Fight(Side(Minion(8, 4, cardId: Ids.Plaguerunner, tribes: Tribe.Undead, golden: true), Minion(1, 1, cardId: "U", tribes: Tribe.Undead)),
                           Side(Killer(4, 8), Wall), false, Only(typeof(Plaguerunner)), 0);

        Assert.Equal(["U 5/1"], Board(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Goldrinn

    [Fact]
    public void Goldrinn_gives_beasts_seven_seven_including_beasts_summoned_later()
    {
        // 1. Their K1 7/7 → Goldrinn (pick 0): both die. Beasts +7/+7 until next turn: B 1/1 Reborn → 8/8; X unchanged.
        // 2. Our B attacks K2 8/8 (pick 0): both die. B's reborn copy (printed 1/1) gets +7/+7 on arrival: 8/8.
        var result = Fight(
            Side(Minion(7, 7, cardId: Ids.Goldrinn, tribes: Tribe.Beast), Minion(1, 1, Keyword.Reborn, "B", Tribe.Beast), Minion(1, 1, cardId: "X")),
            Side(Killer(7, 7), Killer(8, 8), Wall), false, Only(typeof(Goldrinn)));

        Assert.Equal(["B 8/8", "X 1/1"], Board(result.Ours));
        Assert.Contains("reborn our B 8/8 at 0", result.Log);
    }

    [Fact]
    public void Golden_goldrinn_gives_fourteen_fourteen()
    {
        // Golden text: "+14/+14". B 1/1 → 15/15.
        var result = Fight(Side(Minion(14, 14, cardId: Ids.Goldrinn, tribes: Tribe.Beast, golden: true), Minion(1, 1, cardId: "B", tribes: Tribe.Beast)),
                           Side(Killer(14, 14), Wall), false, Only(typeof(Goldrinn)), 0);

        Assert.Equal(["B 15/15"], Board(result.Ours));
    }

    [Fact]
    public void Goldrinn_reads_its_bonus_from_script_data()
    {
        // The text's {0}/{1} are TAG_SCRIPT_DATA_NUM_1/2: captured 10/9 → B 1/1 → 11/10.
        var goldrinn = Minion(7, 7, cardId: Ids.Goldrinn, tribes: Tribe.Beast) with { ScriptData1 = 10, ScriptData2 = 9 };
        var result = Fight(Side(goldrinn, Minion(1, 1, cardId: "B", tribes: Tribe.Beast)), Side(Killer(7, 7), Wall), false, Only(typeof(Goldrinn)), 0);

        Assert.Equal(["B 11/10"], Board(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Ship Master Eudora

    [Fact]
    public void Eudora_gives_your_minions_six_six()
    {
        // Killer 3/8 → Eudora 8/3 (pick 0): both die. A 1/1 → 7/7, B 2/2 → 8/8.
        var result = Fight(Side(Minion(8, 3, cardId: Ids.Eudora, tribes: Tribe.Pirate), Minion(1, 1, cardId: "A"), Minion(2, 2, cardId: "B")),
                           Side(Killer(3, 8), Wall), false, Only(typeof(ShipMasterEudora)), 0);

        Assert.Equal(["A 7/7", "B 8/8"], Board(result.Ours));
    }

    [Fact]
    public void Golden_eudora_gives_six_six_twice()
    {
        // Golden text: "Give your minions +{0}/+{1} twice" with {0}/{1} = 6/6 (not 12/12 once): A 1/1 → 13/13, B → 14/14.
        var eudora = Minion(16, 6, cardId: Ids.Eudora, tribes: Tribe.Pirate, golden: true) with { ScriptData1 = 6, ScriptData2 = 6 };
        var result = Fight(Side(eudora, Minion(1, 1, cardId: "A"), Minion(2, 2, cardId: "B")), Side(Killer(6, 16), Wall), false,
                           Only(typeof(ShipMasterEudora)), 0);

        Assert.Equal(["A 13/13", "B 14/14"], Board(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Showy Cyclist

    [Fact]
    public void Cyclist_gives_your_naga_two_one()
    {
        // Killer 2/4 → Cyclist 4/2 (pick 0). Naga +2/+1: N 1/1 → 3/2. X is not a Naga.
        var result = Fight(Side(Minion(4, 2, cardId: Ids.Cyclist, tribes: Tribe.Naga), Minion(1, 1, cardId: "N", tribes: Tribe.Naga), Minion(1, 1, cardId: "X")),
                           Side(Killer(2, 4), Wall), false, Only(typeof(ShowyCyclist)), 0);

        Assert.Equal(["N 3/2", "X 1/1"], Board(result.Ours));
    }

    [Fact]
    public void Improved_cyclist_reads_its_attack_bonus_from_script_data()
    {
        // {1} = TAG_SCRIPT_DATA_NUM_2 = 6 (improved twice); the Health part ({3}) is not captured and is taken as half: +6/+3.
        var cyclist = Minion(4, 2, cardId: Ids.Cyclist, tribes: Tribe.Naga) with { ScriptData1 = 3, ScriptData2 = 6 };
        var result = Fight(Side(cyclist, Minion(1, 1, cardId: "N", tribes: Tribe.Naga)), Side(Killer(2, 4), Wall), false, Only(typeof(ShowyCyclist)), 0);

        Assert.Equal(["N 7/4"], Board(result.Ours));
    }

    [Fact]
    public void Golden_cyclist_gives_four_two()
    {
        // Golden text: "+4/+2". N 1/1 → 5/3.
        var result = Fight(Side(Minion(8, 4, cardId: Ids.Cyclist, tribes: Tribe.Naga, golden: true), Minion(1, 1, cardId: "N", tribes: Tribe.Naga)),
                           Side(Killer(4, 8), Wall), false, Only(typeof(ShowyCyclist)), 0);

        Assert.Equal(["N 5/3"], Board(result.Ours));
    }
}
