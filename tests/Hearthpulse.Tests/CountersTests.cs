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
          {"id":"BE_014","dbfId":120005,"name":"Spawn of C'Thun","type":"MINION","cardClass":"NEUTRAL","cost":4}
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
