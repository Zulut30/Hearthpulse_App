using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects.Deaths;
using static Hearthlens.Tests.Sim.Deaths.DeathsTestKit;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Deaths;

/// <summary>Deathrattles that summon fixed tokens. Unless noted: their Killer attacks first and trades with our minion.</summary>
public sealed class TokenSummonTests
{
    // ------------------------------------------------------------------------------------------- Cord Puller

    [Fact]
    public void Cord_puller_summons_a_microbot()
    {
        // 1/1 (Divine Shield left off so one hit kills it) trades with a 1/1. Deathrattle: a 1/1 Microbot in its slot.
        var result = Fight(Side(Minion(1, 1, cardId: Ids.CordPuller, tribes: Tribe.Mech)), Side(Killer(1, 1), Wall), false, Only(typeof(CordPuller)));

        Assert.Equal(["BG_BOT_312t 1/1"], Board(result.Ours));
    }

    [Fact]
    public void Golden_cord_puller_summons_a_two_two_microbot()
    {
        // Golden text: "Summon a 2/2 Microbot" (the golden Microbot).
        var result = Fight(Side(Minion(2, 2, cardId: Ids.CordPuller, tribes: Tribe.Mech, golden: true)), Side(Killer(2, 2), Wall), false,
                           Only(typeof(CordPuller)));

        Assert.Equal(["BG_BOT_312t(G) 2/2"], Board(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Harmless Bonehead

    [Fact]
    public void Bonehead_summons_two_skeletons()
    {
        var result = Fight(Side(Minion(1, 1, cardId: Ids.Bonehead, tribes: Tribe.Undead)), Side(Killer(1, 1), Wall), false,
                           Only(typeof(HarmlessBonehead)));

        Assert.Equal(Times(2, "BG_ICC_026t 1/1"), Board(result.Ours));
    }

    [Fact]
    public void Golden_bonehead_summons_four_skeletons()
    {
        // Golden text: "Summon four 1/1 Skeletons" (normal Skeletons, not 2/2 ones).
        var result = Fight(Side(Minion(2, 2, cardId: Ids.Bonehead, tribes: Tribe.Undead, golden: true)), Side(Killer(2, 2), Wall), false,
                           Only(typeof(HarmlessBonehead)));

        Assert.Equal(Times(4, "BG_ICC_026t 1/1"), Board(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Underrot Spawn

    [Fact]
    public void Underrot_spawn_summons_a_tentacle_then_gives_every_minion_one_attack()
    {
        // Killer → Spawn (pick 0). Tentacle 0/2 Taunt in slot 0, then +1 Attack to all: Tentacle 1/2, X 3/3 → 4/3.
        var result = Fight(Side(Minion(2, 2, cardId: Ids.Underrot, tribes: Tribe.Aberration), Minion(3, 3, cardId: "X")), Side(Killer(2, 2), Wall),
                           false, Only(typeof(UnderrotSpawn)), 0);

        Assert.Equal(["BGFYM_002t 1/2 Taunt", "X 4/3"], Board(result.Ours));
    }

    [Fact]
    public void Golden_underrot_spawn_summons_two_tentacles_and_gives_two_attack()
    {
        // Golden text: two 0/2 Tentacles, +2 Attack: Tentacles 2/2, X 3/3 → 5/3.
        var result = Fight(Side(Minion(4, 4, cardId: Ids.Underrot, tribes: Tribe.Aberration, golden: true), Minion(3, 3, cardId: "X")),
                           Side(Killer(4, 4), Wall), false, Only(typeof(UnderrotSpawn)), 0);

        Assert.Equal(["BGFYM_002t 2/2 Taunt", "BGFYM_002t 2/2 Taunt", "X 5/3"], Board(result.Ours));
    }

    [Fact]
    public void Underrot_spawn_reads_its_attack_bonus_from_script_data()
    {
        // The text's {0} is TAG_SCRIPT_DATA_NUM_1: a captured 3 means +3 Attack. Tentacle 3/2, X 6/3.
        var spawn = Minion(2, 2, cardId: Ids.Underrot, tribes: Tribe.Aberration) with { ScriptData1 = 3 };
        var result = Fight(Side(spawn, Minion(3, 3, cardId: "X")), Side(Killer(2, 2), Wall), false, Only(typeof(UnderrotSpawn)), 0);

        Assert.Equal(["BGFYM_002t 3/2 Taunt", "X 6/3"], Board(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Cadaver Caretaker

    [Fact]
    public void Caretaker_summons_three_skeletons()
    {
        var result = Fight(Side(Minion(3, 3, cardId: Ids.Caretaker, tribes: Tribe.Undead)), Side(Killer(3, 3), Wall), false,
                           Only(typeof(CadaverCaretaker)));

        Assert.Equal(Times(3, "BG_ICC_026t 1/1"), Board(result.Ours));
    }

    [Fact]
    public void Golden_caretaker_summons_six_skeletons()
    {
        var result = Fight(Side(Minion(6, 6, cardId: Ids.Caretaker, tribes: Tribe.Undead, golden: true)), Side(Killer(6, 6), Wall), false,
                           Only(typeof(CadaverCaretaker)));

        Assert.Equal(Times(6, "BG_ICC_026t 1/1"), Board(result.Ours));
    }

    [Fact]
    public void Golden_caretaker_stops_at_seven_minions()
    {
        // Killer → Caretaker (pick 0). Two minions stay, so only 5 of the 6 Skeletons fit, in the Caretaker's slot (left of X).
        var result = Fight(
            Side(Minion(6, 6, cardId: Ids.Caretaker, tribes: Tribe.Undead, golden: true), Minion(0, 5, cardId: "X"), Minion(0, 5, cardId: "Y")),
            Side(Killer(6, 6), Wall), false, Only(typeof(CadaverCaretaker)), 0);

        Assert.Equal(Times(5, "BG_ICC_026t 1/1").Concat(["X 0/5", "Y 0/5"]), Board(result.Ours));
        Assert.Single(result.Log, l => l == "no room to summon BG_ICC_026t for us");
    }

    // ------------------------------------------------------------------------------------------- Handless Forsaken

    [Fact]
    public void Handless_forsaken_summons_a_hand_with_reborn()
    {
        // 2/1 vs a 1/2: both die. Deathrattle: a 2/1 Hand with Reborn.
        var result = Fight(Side(Minion(2, 1, cardId: Ids.Handless, tribes: Tribe.Undead)), Side(Killer(1, 2), Wall), false,
                           Only(typeof(HandlessForsaken)));

        Assert.Equal(["BG25_010t 2/1 Reborn"], Board(result.Ours));
    }

    [Fact]
    public void Golden_handless_forsaken_summons_two_normal_hands()
    {
        // Golden text: "Summon two 2/1 Hands with Reborn".
        var result = Fight(Side(Minion(4, 2, cardId: Ids.Handless, tribes: Tribe.Undead, golden: true)), Side(Killer(2, 4), Wall), false,
                           Only(typeof(HandlessForsaken)));

        Assert.Equal(Times(2, "BG25_010t 2/1 Reborn"), Board(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- Sewer Lord and Sewer Rat

    [Fact]
    public void Sewer_lord_summons_two_sewer_rats()
    {
        // 4/6 vs a 6/4: both die. Deathrattle: two 3/2 Sewer Rats.
        var result = Fight(Side(Minion(4, 6, cardId: Ids.SewerLord, tribes: Tribe.Beast)), Side(Killer(6, 4), Wall), false, Only(typeof(SewerLord)));

        Assert.Equal(Times(2, "BG19_010 3/2"), Board(result.Ours));
    }

    [Fact]
    public void Golden_sewer_lord_summons_two_golden_sewer_rats()
    {
        // Golden text: "two Golden Sewer Rats" (6/4).
        var result = Fight(Side(Minion(8, 12, cardId: Ids.SewerLord, tribes: Tribe.Beast, golden: true)), Side(Killer(12, 8), Wall), false,
                           Only(typeof(SewerLord)));

        Assert.Equal(Times(2, "BG19_010(G) 6/4"), Board(result.Ours));
    }

    [Fact]
    public void Sewer_rat_summons_a_half_shell()
    {
        // 3/2 vs a 2/3: both die. Deathrattle: a 2/3 Turtle (Half-Shell) with Taunt.
        var result = Fight(Side(Minion(3, 2, cardId: Ids.SewerRat, tribes: Tribe.Beast)), Side(Killer(2, 3), Wall), false, Only(typeof(SewerRat)));

        Assert.Equal(["BG19_010t 2/3 Taunt"], Board(result.Ours));
    }

    [Fact]
    public void Golden_sewer_rat_summons_a_four_six_half_shell()
    {
        var result = Fight(Side(Minion(6, 4, cardId: Ids.SewerRat, tribes: Tribe.Beast, golden: true)), Side(Killer(4, 6), Wall), false,
                           Only(typeof(SewerRat)));

        Assert.Equal(["BG19_010t(G) 4/6 Taunt"], Board(result.Ours));
    }

    [Fact]
    public void Sewer_lords_rats_summon_half_shells_when_they_die()
    {
        // Killer trades with the Lord → rats R1, R2. Our R1 (slot 0) attacks their taunting 2/3 and both die:
        // R1's Half-Shell takes its slot, left of R2.
        var result = Fight(Side(Minion(4, 6, cardId: Ids.SewerLord, tribes: Tribe.Beast)), Side(Killer(6, 4), Killer(2, 3, Keyword.Taunt), Wall), false,
                           Only(typeof(SewerLord), typeof(SewerRat)));

        Assert.Equal(["BG19_010t 2/3 Taunt", "BG19_010 3/2"], Board(result.Ours));
    }
}
