using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Tracking.Counters;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// Counters read the tags the client keeps on each player (seen in real Power.logs of October 2026: CTHUN_ABILITY*_USED,
/// HERALD_COLOSSAL_AMOUNT, QUEST_PROGRESS) and what the tracker records itself (spells by school, minions summoned).
/// The mulligan panel shows community keep/win rates while the opening hand is chosen.
/// </summary>
public sealed class CountersTests
{
    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream("""
        [
          {"id":"HERO_09","dbfId":813,"name":"Anduin","type":"HERO","cardClass":"PRIEST"},
          {"id":"BE_013","dbfId":120001,"name":"C'Thun, Eye of the Empire","type":"MINION","cardClass":"NEUTRAL","cost":10},
          {"id":"CORE_CS2_004","dbfId":120002,"name":"Power Word: Shield","type":"SPELL","cardClass":"PRIEST","cost":1},
          {"id":"BE_012","dbfId":120003,"name":"Dark Deacon","type":"MINION","cardClass":"PRIEST","cost":3},
          {"id":"CATA_480","dbfId":120004,"name":"Some Quest","type":"SPELL","cardClass":"PRIEST","cost":1},
          {"id":"BE_014","dbfId":120005,"name":"Spawn of C'Thun","type":"MINION","cardClass":"NEUTRAL","cost":4},
          {"id":"CS2_087","dbfId":120006,"name":"Blessing of Might","type":"SPELL","cardClass":"PALADIN","cost":1},
          {"id":"CORE_EX1_284","dbfId":120007,"name":"Azure Drake","type":"MINION","cardClass":"NEUTRAL","cost":5,"races":["DRAGON"]},
          {"id":"TLC_515","dbfId":120008,"name":"Cultist Map","type":"SPELL","cardClass":"ROGUE","cost":1,"mechanics":["DISCOVER"]},
          {"id":"BE_204","dbfId":120009,"name":"Gargantuan Slitherer","type":"MINION","cardClass":"NEUTRAL","cost":6},
          {"id":"BE_013t1","dbfId":120010,"name":"Wrath of C'Thun","type":"SPELL","cardClass":"NEUTRAL"},
          {"id":"BE_013t2","dbfId":120011,"name":"Domain of C'Thun","type":"SPELL","cardClass":"NEUTRAL"},
          {"id":"BE_013t3","dbfId":120012,"name":"Glare of C'Thun","type":"SPELL","cardClass":"NEUTRAL"},
          {"id":"BE_013t4","dbfId":120013,"name":"Servants of C'Thun","type":"SPELL","cardClass":"NEUTRAL"}
        ]
        """u8.ToArray()));

    private static (GameState Game, ConstructedTracker Tracker) Play(params string[] bodies)
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        var tracker = new ConstructedTracker();
        parser.AddListener(tracker);
        parser.AddListener(tracker.Counters);
        string[] setup =
        [
            "DebugPrintPower() - CREATE_GAME",
            "DebugPrintPower() -     GameEntity EntityID=1",
            "DebugPrintPower() -         tag=CARDTYPE value=GAME",
            "DebugPrintPower() -         tag=TURN value=3",
            "DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115198130930503 lo=11]",
            "DebugPrintPower() -         tag=CONTROLLER value=1",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -         tag=HERO_ENTITY value=20",
            "DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=144115198130930503 lo=22]",
            "DebugPrintPower() -         tag=CONTROLLER value=2",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() - FULL_ENTITY - Creating ID=20 CardID=HERO_09",
            "DebugPrintPower() -     tag=ZONE value=PLAY",
            "DebugPrintPower() -     tag=CONTROLLER value=1",
            "DebugPrintPower() -     tag=CARDTYPE value=HERO",
            "DebugPrintGame() - GameType=GT_RANKED",
            "DebugPrintGame() - FormatType=FT_STANDARD",
            "DebugPrintGame() - PlayerID=1, PlayerName=velox#21276",
            "DebugPrintGame() - PlayerID=2, PlayerName=UNKNOWN HUMAN PLAYER",
            "DebugPrintPower() - TAG_CHANGE Entity=Rival#1234 tag=MULLIGAN_STATE value=DONE ",
            // A known card in hand tells the parser which player is local, as in every real game.
            "DebugPrintPower() - FULL_ENTITY - Creating ID=99 CardID=CORE_CS2_004",
            "DebugPrintPower() -     tag=ZONE value=HAND",
            "DebugPrintPower() -     tag=CONTROLLER value=1",
            "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
        ];
        foreach (var body in setup.Concat(bodies))
        {
            parser.Feed("D 12:30:00.0000000 GameState." + body);
        }

        return (parser.Game!, tracker);
    }

    private static string[] Card(int id, string cardId, string zone, int controller, string type, params string[] tags) =>
    [
        $"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={cardId}",
        $"DebugPrintPower() -     tag=ZONE value={zone}",
        $"DebugPrintPower() -     tag=CONTROLLER value={controller}",
        $"DebugPrintPower() -     tag=CARDTYPE value={type}",
        .. tags.Select(t => "DebugPrintPower() -     " + t),
    ];

    private static string[] PlayFromHand(int id, string cardId, int controller) =>
    [
        $"DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=X id={id} zone=HAND zonePos=1 cardId={cardId} player={controller}] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ",
        $"DebugPrintPower() -     TAG_CHANGE Entity=[entityName=X id={id} zone=HAND zonePos=1 cardId={cardId} player={controller}] tag=ZONE value=PLAY ",
        "DebugPrintPower() - BLOCK_END",
    ];

    private static ConstructedView View((GameState Game, ConstructedTracker Tracker) state) => state.Tracker.BuildView(state.Game, Cards);

    private static string[] Die(int id, string cardId, int controller) =>
        [$"DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id={id} zone=PLAY zonePos=1 cardId={cardId} player={controller}] tag=ZONE value=GRAVEYARD "];

    /// <summary>A card created in hand and played from it (any type), with optional extra tags on the card.</summary>
    private static string[] Cast(int id, string cardId, int controller, string type, params string[] tags) =>
        [.. Card(id, cardId, "HAND", controller, type, tags), .. PlayFromHand(id, cardId, controller)];

    private static string Value(ConstructedView view, string id, bool opponent = false) => view.Counters.Single(c => c.Id == id && c.Opponent == opponent).Value;

    [Fact]
    public void Per_turn_counters_read_the_player_tags()
    {
        var view = View(Play(
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=CURRENT_PLAYER value=1 ",
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=NUM_CARDS_DRAWN_THIS_TURN value=2 ",
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=NUM_CARDS_PLAYED_THIS_TURN value=3 ",
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=NUM_FRIENDLY_MINIONS_THAT_DIED_THIS_TURN value=1 ",
            // The opponent's count from their last turn is not reset yet: it does not count on your turn.
            "DebugPrintPower() - TAG_CHANGE Entity=Rival#1234 tag=NUM_CARDS_PLAYED_THIS_TURN value=4 ",
            "DebugPrintPower() - TAG_CHANGE Entity=Rival#1234 tag=NUM_FRIENDLY_MINIONS_THAT_DIED_THIS_TURN value=2 "));

        Assert.Equal("2", Value(view, "cards-drawn-turn"));
        Assert.Equal("3", Value(view, "cards-played-turn"));
        Assert.Equal("0", Value(view, "cards-played-turn", opponent: true));
        var died = view.Counters.Single(c => c.Id == "minions-died-turn" && !c.Opponent);
        Assert.Equal("3", died.Value);
        Assert.Equal(["Yours: 1"], died.Details);
    }

    [Fact]
    public void Buff_counters_read_the_player_tags()
    {
        var view = View(Play(
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=SILVER_HAND_RECRUIT_HP_BUFF value=1 ",
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=SILVER_HAND_RECRUIT_ATK_BUFF value=2 ",
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=CTHUN_ATTACK_BUFF value=4 ",
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=CTHUN_HEALTH_BUFF value=5 ",
            "DebugPrintPower() - TAG_CHANGE Entity=Rival#1234 tag=INVOKE_COUNTER value=3 "));

        var recruits = view.Counters.Single(c => c.Id == "silver-hand" && !c.Opponent);
        Assert.Equal("+2/+1", recruits.Value);
        Assert.True(recruits.Relevant);
        Assert.Equal("10/11", Value(view, "cthun-old"));
        Assert.Equal("6/6", Value(view, "cthun-old", opponent: true));
        Assert.Equal("3", Value(view, "galakrond", opponent: true));
    }

    [Fact]
    public void Played_cards_are_counted_by_cost_class_and_target()
    {
        var view = View(Play([
            .. Card(40, "CORE_EX1_284", "PLAY", 1, "MINION"),
            .. Cast(41, "CS2_087", 1, "SPELL", "tag=TAG_LAST_KNOWN_COST_IN_HAND value=2", "tag=CARD_TARGET value=40"),
            .. Cast(42, "CORE_CS2_004", 1, "SPELL", "tag=TAG_LAST_KNOWN_COST_IN_HAND value=6", "tag=CARD_TARGET value=20"),
            .. Cast(43, "CORE_CS2_004", 1, "SPELL", "tag=TAG_LAST_KNOWN_COST_IN_HAND value=5"),
            .. Card(44, "BE_012", "PLAY", 2, "MINION"),
            .. Cast(45, "CS2_087", 1, "SPELL", "tag=TAG_LAST_KNOWN_COST_IN_HAND value=2", "tag=CARD_TARGET value=44"),
        ]));

        Assert.Equal("2", Value(view, "cards-2-mana"));
        Assert.Equal("2", Value(view, "expensive-spells"));
        Assert.Equal("2", Value(view, "paladin-cards"));
        Assert.Equal("2", Value(view, "spells-on-friendly"));
        Assert.Equal("3", Value(view, "spells-on-characters"));
    }

    [Fact]
    public void A_card_played_for_zero_counts_at_zero_after_its_cost_goes_back_up()
    {
        // Real log 2026-10-23 18:28: JAIL_720 played with TAG_LAST_KNOWN_COST_IN_HAND=0, then its COST went back to 2.
        var view = View(Play([
            .. Cast(41, "CS2_087", 1, "SPELL", "tag=TAG_LAST_KNOWN_COST_IN_HAND value=2"),
            .. Cast(42, "CS2_087", 1, "SPELL", "tag=TAG_LAST_KNOWN_COST_IN_HAND value=0", "tag=COST value=0"),
            "DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id=42 zone=PLAY zonePos=1 cardId=CS2_087 player=1] tag=COST value=2 ",
        ]));

        Assert.Equal("1", Value(view, "cards-2-mana"));
    }

    [Fact]
    public void Improving_cards_count_earlier_copies()
    {
        var view = View(Play([
            .. Cast(50, "YOG_514", 1, "MINION"),
            .. Cast(51, "YOG_514", 1, "MINION"),
            .. Cast(52, "JAIL_732", 1, "SPELL"),
            .. Cast(53, "BOT_283", 1, "MINION"),
            .. Cast(54, "TIME_005t1", 2, "MINION"),
            .. Cast(55, "TIME_005t1", 2, "MINION"),
            .. Cast(56, "TIME_005t4", 2, "MINION"),
            .. Card(57, "TTN_401", "PLAY", 1, "MINION"),
            .. Card(58, "CORE_EX1_284", "DECK", 1, "MINION"),
            "DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id=58 zone=DECK zonePos=0 cardId=CORE_EX1_284 player=1] tag=ZONE value=PLAY ",
            "DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id=57 zone=SETASIDE zonePos=0 cardId=TTN_401 player=1] tag=ZONE value=SETASIDE ",
            "DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id=57 zone=SETASIDE zonePos=0 cardId=TTN_401 player=1] tag=ZONE value=PLAY ",
        ]));

        Assert.Equal("3-Cost Spell", Value(view, "chaotic-tendril"));
        Assert.Equal("2-Cost Demon", Value(view, "void-soul"));
        Assert.Equal("+2/+2", Value(view, "pogo-hopper"));
        var rafaam = view.Counters.Single(c => c.Id == "timethief-rafaam" && c.Opponent);
        Assert.Equal("2/9", rafaam.Value);
        Assert.Equal(["3 Rafaams played."], rafaam.Details);
        Assert.True(rafaam.Relevant);
        Assert.Equal("+1/+1", Value(view, "astral-automaton"));
        Assert.Equal("1", Value(view, "dragons-summoned"));
    }

    [Fact]
    public void Friendly_deaths_count_toward_card_goals()
    {
        var view = View(Play([
            .. Card(60, "JAIL_974", "PLAY", 1, "MINION"),
            .. Card(61, "JAIL_974", "PLAY", 1, "MINION"),
            .. Card(62, "SW_075", "PLAY", 1, "MINION"),
            .. Card(63, "EDR_465", "PLAY", 2, "MINION"),
            .. Die(60, "JAIL_974", 1),
            .. Die(61, "JAIL_974", 1),
            .. Die(62, "SW_075", 1),
            .. Die(63, "EDR_465", 2),
        ]));

        var archmage = view.Counters.Single(c => c.Id == "captured-archmage" && !c.Opponent);
        Assert.Equal("2/4", archmage.Value);
        Assert.Equal(0.5, archmage.Progress);
        Assert.Equal("1/10", Value(view, "elwynn-boar"));
        Assert.Equal("1", Value(view, "ysondre", opponent: true));
    }

    [Fact]
    public void Card_tables_add_up_plays_and_deathrattles()
    {
        var view = View(Play([
            // Interstellar Wayfarer: Battlecry and Deathrattle reduce Librams by 1 each; Aldor Truthseeker by 2.
            .. Cast(70, "GDB_721", 1, "MINION"),
            .. Die(70, "GDB_721", 1),
            .. Cast(71, "BT_026", 1, "MINION"),
            .. Cast(72, "DRG_218", 2, "MINION"),
            .. Cast(73, "DRG_303", 2, "MINION"),
            .. Cast(74, "MEND_501", 1, "MINION"),
            .. Cast(75, "MEND_503", 1, "MINION"),
            .. Cast(76, "TSC_955", 2, "MINION"),
            .. Cast(77, "MEND_307", 1, "SPELL"),
            .. Cast(78, "GDB_434", 1, "MINION"),
            .. Cast(79, "WW_001", 2, "MINION"),
            .. Cast(80, "BE_283", 1, "MINION"),
        ]));

        Assert.Equal("-4", Value(view, "libram"));
        Assert.Equal("3", Value(view, "galakrond", opponent: true));
        Assert.Equal("-1", Value(view, "leyline-cost"));
        Assert.Equal("+1", Value(view, "leyline-trigger"));
        Assert.Equal("+0", Value(view, "leyline-effect"));
        Assert.Equal("Next: 2", Value(view, "abyssal-curse", opponent: true));
        Assert.Equal("+2 Cost", Value(view, "animal-companion"));
        Assert.Equal("3 damage", Value(view, "asteroid"));
        Assert.Equal("1", Value(view, "excavate", opponent: true));
        Assert.Equal("3", Value(view, "blood-runes"));
    }

    [Fact]
    public void Protoss_counters_follow_the_starcraft_cards()
    {
        var view = View(Play([
            .. Card(81, "BE_012", "PLAY", 2, "MINION"),
            // Photon Cannon that kills a minion discounts Protoss minions; one that doesn't, doesn't.
            .. Card(82, "SC_753", "HAND", 1, "SPELL"),
            "DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=X id=82 zone=HAND zonePos=1 cardId=SC_753 player=1] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ",
            "DebugPrintPower() -     TAG_CHANGE Entity=[entityName=X id=82 zone=HAND zonePos=1 cardId=SC_753 player=1] tag=ZONE value=PLAY ",
            .. Die(81, "BE_012", 2),
            "DebugPrintPower() - BLOCK_END",
            .. Cast(83, "SC_753", 1, "SPELL"),
            .. Cast(84, "SC_755", 1, "SPELL"),
            .. Card(85, "SC_764", "PLAY", 1, "MINION"),
            .. Die(85, "SC_764", 1),
            .. Cast(86, "SC_754", 1, "HERO"),
        ]));

        Assert.Equal("-4", Value(view, "protoss-discount"));
        Assert.Equal("3", Value(view, "colossus"));
    }

    [Fact]
    public void Hero_attacks_and_damage_on_own_turn_are_counted()
    {
        var view = View(Play(
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=CURRENT_PLAYER value=1 ",
            "DebugPrintPower() - BLOCK_START BlockType=ATTACK Entity=[entityName=Anduin id=20 zone=PLAY zonePos=0 cardId=HERO_09 player=1] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ",
            "DebugPrintPower() -     TAG_CHANGE Entity=[entityName=Anduin id=20 zone=PLAY zonePos=0 cardId=HERO_09 player=1] tag=DAMAGE value=3 ",
            "DebugPrintPower() - BLOCK_END",
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=CURRENT_PLAYER value=0 ",
            // Damage on the opponent's turn does not count.
            "DebugPrintPower() - TAG_CHANGE Entity=[entityName=Anduin id=20 zone=PLAY zonePos=0 cardId=HERO_09 player=1] tag=DAMAGE value=5 ",
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=CURRENT_PLAYER value=1 ",
            "DebugPrintPower() - TAG_CHANGE Entity=[entityName=Anduin id=20 zone=PLAY zonePos=0 cardId=HERO_09 player=1] tag=DAMAGE value=4 ",
            "DebugPrintPower() - TAG_CHANGE Entity=[entityName=Anduin id=20 zone=PLAY zonePos=0 cardId=HERO_09 player=1] tag=DAMAGE value=6 "));

        Assert.Equal("1", Value(view, "hero-attacks"));
        Assert.Equal("5", Value(view, "self-damage"));
        Assert.Equal("1", Value(view, "friendly-attacks"));
    }

    [Fact]
    public void Discovers_count_your_picks_from_discover_cards()
    {
        string[] Choice(int id, int source, string sourceCard) =>
        [
            $"DebugPrintEntityChoices() - id={id} Player=velox#21276 TaskList=272 ChoiceType=GENERAL CountMin=1 CountMax=1",
            $"DebugPrintEntityChoices() -   Source=[entityName=X id={source} zone=PLAY zonePos=0 cardId={sourceCard} player=1]",
            "DebugPrintEntityChoices() -   Entities[0]=[entityName=Y id=99 zone=HAND zonePos=1 cardId=CORE_CS2_004 player=1]",
            $"DebugPrintEntitiesChosen() - id={id} Player=velox#21276 EntitiesCount=1",
        ];

        var view = View(Play([
            .. Card(90, "TLC_515", "PLAY", 1, "SPELL"),
            .. Card(91, "BE_204", "PLAY", 1, "MINION"),
            .. Card(92, "BE_014", "PLAY", 1, "MINION", "tag=DISCOVER value=1"),
            .. Choice(3, 90, "TLC_515"),
            .. Choice(4, 91, "BE_204"),
            .. Choice(5, 92, "BE_014"),
        ]));

        var discovers = view.Counters.Single(c => c.Id == "discovers");
        Assert.False(discovers.Opponent);
        Assert.Equal("2", discovers.Value);
        Assert.Equal(["This turn: 2"], discovers.Details);
    }

    [Fact]
    public void Countdowns_triggers_and_repeatable_quests_follow_the_turns()
    {
        string[] bodies =
        [
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=FIRST_PLAYER value=1 ",
            .. Cast(100, "TOY_529", 1, "SPELL"),
            .. Cast(101, "EDR_464", 1, "MINION"),
            .. Cast(102, "CORE_CS2_004", 1, "SPELL"),
            .. Card(103, "VAC_321", "PLAY", 1, "MINION"),
            "DebugPrintPower() - BLOCK_START BlockType=TRIGGER Entity=[entityName=X id=103 zone=PLAY zonePos=1 cardId=VAC_321 player=1] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 TriggerKeyword=TRIGGER_VISUAL",
            "DebugPrintPower() - BLOCK_END",
            .. Card(104, "TLC_426", "SECRET", 1, "SPELL", "tag=QUEST_PROGRESS_TOTAL value=6"),
            "DebugPrintPower() - TAG_CHANGE Entity=[entityName=Q id=104 zone=SECRET zonePos=0 cardId=TLC_426 player=1] tag=QUEST_PROGRESS value=5 ",
            "DebugPrintPower() - TAG_CHANGE Entity=[entityName=Q id=104 zone=SECRET zonePos=0 cardId=TLC_426 player=1] tag=QUEST_PROGRESS value=6 ",
            "DebugPrintPower() - TAG_CHANGE Entity=[entityName=Q id=104 zone=SECRET zonePos=0 cardId=TLC_426 player=1] tag=QUEST_PROGRESS value=0 ",
            // Two turns later (one of yours): one turn of the Wheel has passed.
            "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=5 ",
        ];
        var view = View(Play(bodies));

        var wheel = view.Counters.Single(c => c.Id == "wheel-of-death" && !c.Opponent);
        Assert.Equal("4 turns", wheel.Value);
        Assert.True(wheel.Relevant);
        Assert.Equal("Not played", Value(view, "aviana"));
        Assert.Equal("2 left", Value(view, "tyrande"));
        Assert.Equal("2 damage", Value(view, "incindius"));
        Assert.Equal("+1/+1", Value(view, "golakka"));
        var ceaseless = view.Counters.Single(c => c.Id == "ceaseless-expanse" && !c.Opponent);
        Assert.Equal("Cost 97", ceaseless.Value);

        // Five of your turns after the Wheel was cast.
        var later = View(Play([.. bodies, "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=13 "]));
        Assert.Equal("Destroyed", Value(later, "wheel-of-death"));
    }

    [Fact]
    public void Cthun_abilities_count_toward_four()
    {
        var view = View(Play([
            .. Card(10, "BE_013", "DECK", 1, "MINION"),
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=CTHUN_ABILITY2_USED value=1 ",
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=CTHUN_ABILITY4_USED value=1 ",
        ]));

        var cthun = Assert.Single(view.Counters, c => c.Id == "cthun" && !c.Opponent);
        Assert.Equal("2/4", cthun.Value);
        Assert.Equal(0.5, cthun.Progress);
        Assert.True(cthun.Relevant);
        Assert.Equal("BE_013", cthun.IconCardId);
    }

    [Fact]
    public void Cthun_tooltip_lists_the_four_abilities_and_which_triggered()
    {
        var view = View(Play([
            .. Card(10, "BE_013", "DECK", 1, "MINION"),
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=CTHUN_ABILITY2_USED value=1 ",
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=CTHUN_ABILITY4_USED value=1 ",
        ]));

        var cthun = view.Counters.Single(c => c.Id == "cthun" && !c.Opponent);
        Assert.Equal(4, cthun.Goal);
        Assert.Equal(["BE_013t1", "BE_013t2", "BE_013t3", "BE_013t4"], cthun.Parts.Select(p => p.CardId));
        Assert.Equal(["Wrath of C'Thun", "Domain of C'Thun", "Glare of C'Thun", "Servants of C'Thun"], cthun.Parts.Select(p => p.Name));
        Assert.Equal([false, true, false, true], cthun.Parts.Select(p => p.Done!.Value));
        Assert.Contains(cthun.RelatedCards, c => c.CardId == "BE_013");
        Assert.All(view.Counters.Single(c => c.Id == "cthun" && c.Opponent).Parts, p => Assert.False(p.Done));
    }

    [Fact]
    public void Related_cards_come_with_the_copies_in_the_deck()
    {
        var state = Play(Card(10, "BE_012", "PLAY", 1, "MINION"));
        state.Tracker.Deck = new DeckDefinition("Test", 1, string.Empty, "FT_STANDARD", [813], new Dictionary<int, int> { [120003] = 2, [120005] = 1 });

        var cthun = View(state).Counters.Single(c => c.Id == "cthun" && !c.Opponent);
        Assert.Equal([("BE_012", "Dark Deacon", 2), ("BE_014", "Spawn of C'Thun", 1)], cthun.RelatedCards.Select(c => (c.CardId, c.Name, c.Count)));
        Assert.Empty(View(state).Counters.Single(c => c.Id == "cthun" && c.Opponent).RelatedCards);
    }

    [Fact]
    public void Counted_cards_are_grouped_in_the_order_they_were_counted()
    {
        var view = View(Play([
            .. Cast(41, "CS2_087", 1, "SPELL", "tag=SPELL_SCHOOL value=5"),
            .. Cast(42, "CORE_CS2_004", 1, "SPELL", "tag=SPELL_SCHOOL value=6"),
            .. Cast(43, "CS2_087", 1, "SPELL", "tag=SPELL_SCHOOL value=5"),
            .. Card(60, "JAIL_974", "PLAY", 1, "MINION"),
            .. Die(60, "JAIL_974", 1),
            // A card the opponent played that is still hidden counts, but cannot be listed.
            "DebugPrintPower() - FULL_ENTITY - Creating ID=70 CardID=",
            "DebugPrintPower() -     tag=ZONE value=HAND",
            "DebugPrintPower() -     tag=CONTROLLER value=2",
            .. PlayFromHand(70, string.Empty, 2),
        ]));

        var spells = view.Counters.Single(c => c.Id == "spells-cast" && !c.Opponent);
        Assert.Equal([("CS2_087", "Blessing of Might", 2), ("CORE_CS2_004", "Power Word: Shield", 1)], spells.Counted.Select(c => (c.CardId, c.Name, c.Count)));
        Assert.Equal(["CS2_087"], view.Counters.Single(c => c.Id == "spells-holy" && !c.Opponent).Counted.Select(c => c.CardId));
        Assert.Equal(["CS2_087", "CORE_CS2_004"], view.Counters.Single(c => c.Id == "spell-schools" && !c.Opponent).Counted.Select(c => c.CardId));
        var archmage = view.Counters.Single(c => c.Id == "captured-archmage" && !c.Opponent);
        Assert.Equal(4, archmage.Goal);
        Assert.Equal(1, Assert.Single(archmage.Counted).Count);
        var opponent = view.Counters.Single(c => c.Id == "cards-played" && c.Opponent);
        Assert.Equal("1", opponent.Value);
        Assert.Empty(opponent.Counted);
        Assert.Empty(spells.Parts);
    }

    [Fact]
    public void Opponent_counters_show_once_under_way()
    {
        var view = View(Play("DebugPrintPower() - TAG_CHANGE Entity=Rival#1234 tag=HERALD_COLOSSAL_AMOUNT value=3 "));

        var herald = Assert.Single(view.Counters, c => c.Id == "herald" && c.Opponent);
        Assert.Equal("3", herald.Value);
        Assert.True(herald.Relevant);
        Assert.False(view.Counters.Single(c => c.Id == "herald" && !c.Opponent).Relevant);
    }

    [Fact]
    public void Spells_are_counted_by_school_and_quests_by_progress()
    {
        var view = View(Play([
            .. Card(11, "CORE_CS2_004", "HAND", 1, "SPELL", "tag=SPELL_SCHOOL value=5"),
            .. Card(12, "CORE_CS2_004", "HAND", 1, "SPELL", "tag=SPELL_SCHOOL value=6"),
            .. Card(13, "CATA_480", "SECRET", 2, "SPELL", "tag=QUEST_PROGRESS_TOTAL value=3"),
            .. PlayFromHand(11, "CORE_CS2_004", 1),
            .. PlayFromHand(12, "CORE_CS2_004", 1),
            "DebugPrintPower() - TAG_CHANGE Entity=[entityName=Q id=13 zone=SECRET zonePos=0 cardId=CATA_480 player=2] tag=QUEST_PROGRESS value=2 ",
        ]));

        var schools = view.Counters.Single(c => c.Id == "spell-schools" && !c.Opponent);
        Assert.Equal("2", schools.Value);
        Assert.Equal(["Holy", "Shadow"], schools.Details);
        Assert.Equal("1", view.Counters.Single(c => c.Id == "spells-holy" && !c.Opponent).Value);
        var quest = Assert.Single(view.Counters, c => c.Id == Counters.QuestsId);
        Assert.True(quest.Opponent);
        Assert.Equal("2/3", quest.Value);
        Assert.Equal("Some Quest", quest.Title);
    }

    [Fact]
    public void Counter_modes_default_to_auto()
    {
        Assert.Equal(CounterMode.Auto, Counters.ModeOf(null, "cthun"));
        Assert.Equal(CounterMode.Off, Counters.ModeOf(new Dictionary<string, string> { ["cthun"] = "off" }, "cthun"));
        Assert.Contains(Counters.All, c => c.Id == Counters.QuestsId);
        Assert.Equal(Counters.All.Count, Counters.All.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Mulligan_shows_keep_and_win_rates_for_the_matching_archetype()
    {
        var archetype = new MetaArchetype(1, "cthun-priest", "C'Thun Priest", "PRIEST", 1000, 550, 0.55, 0.1, 0, 0, 0, 0,
            ["BE_012", "BE_014"],
            [new MetaMulliganCard("BE_012", 400, 0.8, 300, 0.6), new MetaMulliganCard("BE_014", 10, 0.5, 5, 0.4)]);
        var meta = new ConstructedMeta("standard", "all", "past-7", DateTime.UtcNow, 1000, [archetype], []);
        var state = Play([
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=MULLIGAN_STATE value=INPUT ",
            .. Card(30, "BE_012", "HAND", 1, "MINION", "tag=ZONE_POSITION value=1"),
            .. Card(31, "BE_014", "HAND", 1, "MINION", "tag=ZONE_POSITION value=2"),
            .. Card(32, "GAME_005", "HAND", 1, "SPELL", "tag=ZONE_POSITION value=3"),
        ]);
        state.Tracker.Deck = new DeckDefinition("Mine", 1, "code", "Standard", [813], new Dictionary<int, int> { [120003] = 2, [120005] = 2 });
        state.Tracker.Meta = meta;

        var mulligan = View(state).Mulligan;

        Assert.NotNull(mulligan);
        Assert.Equal("C'Thun Priest", mulligan.Context);
        Assert.DoesNotContain(mulligan.Cards, c => c.CardId == "GAME_005");
        var deacon = mulligan.Cards.Single(c => c.CardId == "BE_012");
        Assert.Equal(0.8, deacon.KeepRate);
        Assert.Equal(0.6, deacon.WinRate!.Value, 6);
        Assert.Equal(0.05, deacon.Delta!.Value, 6);
        Assert.Null(mulligan.Cards.Single(c => c.CardId == "BE_014").KeepRate);
    }

    [Fact]
    public void The_log_says_why_the_mulligan_panel_is_shown_or_hidden()
    {
        // A live game (2026-10-23) left no trace of why the panel was not seen: now each state is one log line.
        string[] hand =
        [
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=MULLIGAN_STATE value=INPUT ",
            .. Card(30, "BE_012", "HAND", 1, "MINION", "tag=ZONE_POSITION value=1"),
            .. Card(31, "BE_014", "HAND", 1, "MINION", "tag=ZONE_POSITION value=2"),
        ];
        var deck = new DeckDefinition("Mine", 1, "code", "Standard", [813], new Dictionary<int, int> { [120003] = 2, [120005] = 2 });

        // From the first INPUT the strip lists the offered cards, also before any numbers arrive.
        var nothing = Play(hand);
        nothing.Tracker.Deck = deck;
        var bare = View(nothing).Mulligan!;
        Assert.Equal("No mulligan stats for this deck yet", bare.NoStats);
        Assert.Equal(["CORE_CS2_004", "BE_012", "BE_014"], bare.Cards.Select(c => c.CardId));
        Assert.All(bare.Cards, c => Assert.Equal(MulliganCall.NoData, c.Call));
        Assert.Equal("mulligan strip without numbers: no API guide for this deck (yet) and the community stats are not loaded",
            Hearthpulse.Core.Tracking.Mulligan.Explain(nothing.Game, bare, deck, hasGuide: false, meta: null));
        nothing.Tracker.Deck = null;
        Assert.Equal("No mulligan stats: Decks.log named no deck for this game", View(nothing).Mulligan!.NoStats);

        var archetype = new MetaArchetype(1, "cthun-priest", "C'Thun Priest", "PRIEST", 1000, 550, 0.55, 0.1, 0, 0, 0, 0,
            ["BE_012", "BE_014"],
            [new MetaMulliganCard("BE_012", 400, 0.8, 300, 0.6), new MetaMulliganCard("BE_014", 10, 0.5, 5, 0.4)]);
        var shown = Play(hand);
        shown.Tracker.Deck = deck;
        shown.Tracker.Meta = new ConstructedMeta("standard", "all", "past-7", DateTime.UtcNow, 1000, [archetype], []);
        var view = View(shown).Mulligan;
        Assert.Equal("mulligan strip shown: C'Thun Priest, keep stats for 1/3 cards (" + view!.Source + ")",
            Hearthpulse.Core.Tracking.Mulligan.Explain(shown.Game, view, deck, hasGuide: false, shown.Tracker.Meta));
        Assert.Contains("Mulligan strip shown: C'Thun Priest, keep stats for 1/3 cards", TestDataFolder.ReadLog());

        var wild = new ConstructedMeta("wild", "all", "past-7", DateTime.UtcNow, 1000, [archetype], []);
        Assert.Equal("mulligan strip without numbers: no API guide for this deck and the community stats are for wild, the game is FT_STANDARD",
            Hearthpulse.Core.Tracking.Mulligan.Explain(shown.Game, null, deck, hasGuide: false, wild));
        Assert.Equal("mulligan strip without numbers: no keep stats for 2/2 cards (Priest, test)",
            Hearthpulse.Core.Tracking.Mulligan.Explain(shown.Game, view with { Context = "Priest", Source = "test", Cards = view.Cards.Where(c => c.KeepRate is null).ToList() }, deck, hasGuide: false, wild));
    }

    [Fact]
    public void Mulligan_panel_closes_once_the_hand_is_kept()
    {
        var state = Play([
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=MULLIGAN_STATE value=DONE ",
            .. Card(30, "BE_012", "HAND", 1, "MINION"),
        ]);
        state.Tracker.Meta = new ConstructedMeta("standard", "all", "past-7", DateTime.UtcNow, 0, [], []);

        Assert.Null(View(state).Mulligan);
    }
}
