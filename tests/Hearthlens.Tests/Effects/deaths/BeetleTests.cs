using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects.Deaths;
using static Hearthlens.Tests.Sim.Deaths.DeathsTestKit;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Deaths;

/// <summary>
/// The Beetle package: Buzzing Vermin, Forest Rover, Turquoise Skitterer and Ravaging Scorpid. "{0}/{1} Beetle" is the
/// summoner's TAG_SCRIPT_DATA_NUM_1/2 (the player's current Beetle). Unless noted, their Killer attacks first and trades
/// with our minion, then only their 0-Attack Wall is left.
/// </summary>
public sealed class BeetleTests
{
    private static SimMinion Beetle(int attack, int health) => Minion(attack, health, cardId: Ids.Beetle, tribes: Tribe.Beast);

    private static SimMinion WithBeetle(SimMinion summoner, int attack, int health) =>
        summoner with { ScriptData1 = attack, ScriptData2 = health };

    // ------------------------------------------------------------------------------------------- Buzzing Vermin

    [Fact]
    public void Vermin_summons_a_beetle_with_the_stats_its_text_shows()
    {
        // Script data 5/4 = "Summon a 5/4 Beetle" (2/2 plus +3/+2 of Beetle upgrades this game). 1/1 Taunt trades with a 1/1.
        var vermin = WithBeetle(Minion(1, 1, Keyword.Taunt, Ids.Vermin, Tribe.Beast), 5, 4);
        var result = Fight(Side(vermin), Side(Killer(1, 1), Wall), false, Only(typeof(BuzzingVermin)));

        Assert.Equal(["BG28_603t 5/4"], Board(result.Ours));
    }

    [Fact]
    public void Vermin_without_script_data_summons_a_two_two_beetle()
    {
        // No script data and no other captured Beetle summoner: the printed "2/2 Beetle".
        var result = Fight(Side(Minion(1, 1, Keyword.Taunt, Ids.Vermin, Tribe.Beast)), Side(Killer(1, 1), Wall), false, Only(typeof(BuzzingVermin)));

        Assert.Equal(["BG28_603t 2/2"], Board(result.Ours));
    }

    [Fact]
    public void Golden_vermin_summons_two_normal_beetles()
    {
        // Golden text: "Summon two {0}/{1} Beetles" with script data 3/3: two normal 3/3 Beetles, not golden ones.
        var vermin = WithBeetle(Minion(2, 2, Keyword.Taunt, Ids.Vermin, Tribe.Beast, golden: true), 3, 3);
        var result = Fight(Side(vermin), Side(Killer(2, 2), Wall), false, Only(typeof(BuzzingVermin)));

        Assert.Equal(Times(2, "BG28_603t 3/3"), Board(result.Ours));
    }

    [Fact]
    public void Beetle_stats_fall_back_to_the_local_players_hand_but_never_the_opponents()
    {
        // Both Vermins have no script data. Ours finds a Forest Rover showing "9/8 Beetle" in our hand (we can see it);
        // theirs must not look at their hand (hidden information) and summons the printed 2/2.
        var hand = WithBeetle(Minion(1, 1, cardId: Ids.Rover, tribes: Tribe.Beast), 9, 8);
        var vermin = Minion(1, 1, cardId: Ids.Vermin, tribes: Tribe.Beast);

        var ours = Fight(Side(vermin) with { Hand = [hand] }, Side(Killer(1, 1), Wall), false, Only(typeof(BuzzingVermin)));
        var theirs = Fight(Side(Killer(1, 1), Wall), Side(vermin) with { Hand = [hand] }, true, Only(typeof(BuzzingVermin)));

        Assert.Equal(["BG28_603t 9/8"], Board(ours.Ours));
        Assert.Equal(["BG28_603t 2/2"], Board(theirs.Theirs));
    }

    // ------------------------------------------------------------------------------------------- Forest Rover

    [Fact]
    public void Rover_summons_a_beetle_with_the_stats_its_text_shows()
    {
        // The Battlecry ("Your Beetles have +2/+1 this game") happened in the tavern; script data 4/3 already includes it.
        var rover = WithBeetle(Minion(1, 1, cardId: Ids.Rover, tribes: Tribe.Beast), 4, 3);
        var result = Fight(Side(rover), Side(Killer(1, 1), Wall), false, Only(typeof(ForestRover)));

        Assert.Equal(["BG28_603t 4/3"], Board(result.Ours));
    }

    [Fact]
    public void Golden_rover_summons_two_beetles()
    {
        // Golden text: "Summon two {0}/{1} Beetles": two normal 4/3 Beetles.
        var rover = WithBeetle(Minion(2, 2, cardId: Ids.Rover, tribes: Tribe.Beast, golden: true), 4, 3);
        var result = Fight(Side(rover), Side(Killer(2, 2), Wall), false, Only(typeof(ForestRover)));

        Assert.Equal(Times(2, "BG28_603t 4/3"), Board(result.Ours));
    }

    [Fact]
    public void Summoner_without_script_data_uses_another_captured_summoners_beetle()
    {
        // Killer → Rover (pick 0). The Rover has no script data; the Vermin next to it shows the player's Beetle, 6/5.
        var vermin = WithBeetle(Minion(1, 1, cardId: Ids.Vermin, tribes: Tribe.Beast), 6, 5);
        var result = Fight(Side(Minion(1, 1, cardId: Ids.Rover, tribes: Tribe.Beast), vermin), Side(Killer(1, 1), Wall), false,
                           Only(typeof(ForestRover)), 0);

        Assert.Equal(["BG28_603t 6/5", "BG31_803 1/1"], Board(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Turquoise Skitterer

    [Fact]
    public void Skitterer_improves_beetles_on_board_then_summons_an_improved_beetle()
    {
        // Killer 5/5 → Skitterer (pick 0); both die. Deathrattle in text order: Beetles +5/+5 this game (our 3/2 Beetle
        // → 8/7), then a 2/2 Beetle that gets the +5/+5 too → 7/7, in the Skitterer's slot.
        var skitterer = WithBeetle(Minion(5, 5, cardId: Ids.Skitterer, tribes: Tribe.Beast), 2, 2);
        var result = Fight(Side(skitterer, Beetle(3, 2)), Side(Killer(5, 5), Wall), false, Only(typeof(TurquoiseSkitterer)), 0);

        Assert.Equal(["BG28_603t 7/7", "BG28_603t 8/7"], Board(result.Ours));
    }

    [Fact]
    public void Golden_skitterer_gives_ten_ten_and_summons_two_beetles()
    {
        // Golden text: "+10/+10 ... Summon two {0}/{1} Beetles": old Beetle 3/2 → 13/12; two 2/2 Beetles → 12/12 each.
        var skitterer = WithBeetle(Minion(10, 10, cardId: Ids.Skitterer, tribes: Tribe.Beast, golden: true), 2, 2);
        var result = Fight(Side(skitterer, Beetle(3, 2)), Side(Killer(10, 10), Wall), false, Only(typeof(TurquoiseSkitterer)), 0);

        Assert.Equal(["BG28_603t 12/12", "BG28_603t 12/12", "BG28_603t 13/12"], Board(result.Ours));
    }

    [Fact]
    public void Skitterer_bonus_reaches_beetles_summoned_later_this_fight()
    {
        // 1. Their K1 5/5 → Skitterer (pick 0): both die; Beetle 2/2+5/5 = 7/7 in slot 0. Board: Beetle 7/7, Vermin 1/1.
        // 2. Our Beetle attacks their Wall (pick 1 of K2, Wall).
        // 3. Their K2 1/1 → Vermin (pick 1): both die; the Vermin's 2/2 Beetle gets the lingering +5/+5 → 7/7.
        var skitterer = WithBeetle(Minion(5, 5, cardId: Ids.Skitterer, tribes: Tribe.Beast), 2, 2);
        var vermin = WithBeetle(Minion(1, 1, cardId: Ids.Vermin, tribes: Tribe.Beast), 2, 2);
        var result = Fight(Side(skitterer, vermin), Side(Killer(5, 5), Killer(1, 1), Wall), false,
                           Only(typeof(TurquoiseSkitterer), typeof(BuzzingVermin)), 0, 1, 1);

        Assert.Equal(Times(2, "BG28_603t 7/7"), Board(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Ravaging Scorpid

    [Fact]
    public void Scorpid_improves_beetles_after_a_friendly_minion_attacks()
    {
        // We attack first: our Beetle 2/2 kills their 1/1 and takes 1 (2/1). After that attack: Beetles +4/+4 → 6/5.
        var result = Fight(Side(Beetle(2, 2), Minion(6, 7, cardId: Ids.Scorpid, tribes: Tribe.Beast)), Side(Minion(1, 1)), true,
                           Only(typeof(RavagingScorpid)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal(["BG28_603t 6/5", "BG36_209 6/7"], Board(result.Ours));
    }

    [Fact]
    public void Golden_scorpid_counts_its_own_attack_and_gives_eight_eight()
    {
        // "A friendly minion" includes the Scorpid: it kills their 1/1 (12/14 → 12/13); Beetle 2/2 + 8/8 → 10/10.
        var result = Fight(Side(Minion(12, 14, cardId: Ids.Scorpid, tribes: Tribe.Beast, golden: true), Beetle(2, 2)), Side(Minion(1, 1)), true,
                           Only(typeof(RavagingScorpid)));

        Assert.Equal(["BG36_209(G) 12/13", "BG28_603t 10/10"], Board(result.Ours));
    }

    [Fact]
    public void Scorpid_bonus_reaches_beetles_summoned_later_this_fight()
    {
        // Our Vermin 1/1 trades with their 1/1. After the attack (before deaths): Beetles +4/+4 this game. Then the
        // Vermin's deathrattle summons a 2/2 Beetle (no script data anywhere) that gets it: 6/6.
        var result = Fight(Side(Minion(1, 1, cardId: Ids.Vermin, tribes: Tribe.Beast), Minion(6, 7, cardId: Ids.Scorpid, tribes: Tribe.Beast)),
                           Side(Minion(1, 1)), true, Only(typeof(BuzzingVermin), typeof(RavagingScorpid)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal(["BG28_603t 6/6", "BG36_209 6/7"], Board(result.Ours));
    }

    [Fact]
    public void Scorpid_deathrattle_summons_a_beetle()
    {
        // Their 7/6 attacks: both die (their attack does not trigger our Scorpid). Deathrattle: a 3/3 Beetle (script data).
        var scorpid = WithBeetle(Minion(6, 7, cardId: Ids.Scorpid, tribes: Tribe.Beast), 3, 3);
        var result = Fight(Side(scorpid), Side(Killer(7, 6), Wall), false, Only(typeof(RavagingScorpid)));

        Assert.Equal(["BG28_603t 3/3"], Board(result.Ours));
    }

    [Fact]
    public void Golden_scorpid_deathrattle_summons_two_beetles()
    {
        // Golden text: "Summon two {0}/{1} Beetles": two normal 2/2 Beetles.
        var scorpid = WithBeetle(Minion(12, 14, cardId: Ids.Scorpid, tribes: Tribe.Beast, golden: true), 2, 2);
        var result = Fight(Side(scorpid), Side(Killer(14, 12), Wall), false, Only(typeof(RavagingScorpid)));

        Assert.Equal(Times(2, "BG28_603t 2/2"), Board(result.Ours));
    }
}
