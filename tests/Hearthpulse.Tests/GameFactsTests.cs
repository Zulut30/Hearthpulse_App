using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Stats;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Tests;

/// <summary>
/// The My Stats fields of a history row, built from synthetic Power.log lines (the grammar of real October 2026 logs):
/// the Battlegrounds hero offer with a reroll, the anomaly, who went first, and the deck id check.
/// </summary>
public sealed class GameFactsTests
{
    private const int Me = 5;
    private const int Them = 6;

    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream("""
        [
          {"id":"HERO_08","dbfId":637,"name":"Jaina","type":"HERO","cardClass":"MAGE"},
          {"id":"HERO_09","dbfId":813,"name":"Anduin","type":"HERO","cardClass":"PRIEST"},
          {"id":"TB_BaconShop_HERO_50","dbfId":59000,"name":"Tess","type":"HERO"},
          {"id":"BG27_Anomaly_100","dbfId":100100,"name":"An Anomaly","type":"BATTLEGROUND_ANOMALY"},
          {"id":"CORE_CS2_029","dbfId":1004,"name":"Fireball","type":"SPELL","cardClass":"MAGE"}
        ]
        """u8.ToArray()));

    private static void Feed(PowerParser parser, IEnumerable<string> bodies)
    {
        foreach (var body in bodies)
        {
            parser.Feed("D 12:30:00.0000000 GameState." + body);
        }

        parser.Flush();
    }

    private static string[] Entity(int id, string cardId, int controller, string zone, string type, params string[] tags) =>
    [
        $"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={cardId}",
        $"DebugPrintPower() -     tag=ZONE value={zone}",
        $"DebugPrintPower() -     tag=CONTROLLER value={controller}",
        $"DebugPrintPower() -     tag=CARDTYPE value={type}",
        .. tags.Select(t => "DebugPrintPower() -     " + t),
    ];

    private static string[] Setup(string gameType, int heroEntity, int opponentHeroEntity, bool opponentIsDummy, params string[] gameTags) =>
    [
        "DebugPrintPower() - CREATE_GAME",
        "DebugPrintPower() -     GameEntity EntityID=1",
        "DebugPrintPower() -         tag=CARDTYPE value=GAME",
        "DebugPrintPower() -         tag=TURN value=5",
        .. gameTags.Select(t => "DebugPrintPower() -         " + t),
        $"DebugPrintPower() -     Player EntityID=2 PlayerID={Me} GameAccountId=[hi=144115198130930503 lo=11]",
        $"DebugPrintPower() -         tag=CONTROLLER value={Me}",
        "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
        $"DebugPrintPower() -         tag=HERO_ENTITY value={heroEntity}",
        $"DebugPrintPower() -     Player EntityID=3 PlayerID={Them} GameAccountId=[hi={(opponentIsDummy ? 0 : 144115198130930503)} lo={(opponentIsDummy ? 0 : 12)}]",
        $"DebugPrintPower() -         tag=CONTROLLER value={Them}",
        "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
        $"DebugPrintPower() -         tag=HERO_ENTITY value={opponentHeroEntity}",
        .. opponentIsDummy ? ["DebugPrintPower() -         tag=BACON_DUMMY_PLAYER value=1"] : Array.Empty<string>(),
        $"DebugPrintGame() - GameType={gameType}",
        "DebugPrintGame() - FormatType=FT_STANDARD",
        $"DebugPrintGame() - PlayerID={Me}, PlayerName=Me#1234",
        $"DebugPrintGame() - PlayerID={Them}, PlayerName=You#5678",
    ];

    private static string Hand(int id, string card, int pos) => $"[entityName=H id={id} zone=HAND zonePos={pos} cardId={card} player={Me}]";

    [Fact]
    public void Battlegrounds_row_keeps_the_hero_offer_with_a_reroll_and_the_anomaly()
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        var battlegrounds = new BattlegroundsTracker { Cards = Cards };
        var facts = new GameFactsTracker();
        parser.AddListener(battlegrounds);
        parser.AddListener(facts);
        Feed(parser,
        [
            .. Setup("GT_BATTLEGROUNDS", 30, 40, opponentIsDummy: true, "tag=BACON_GLOBAL_ANOMALY_DBID value=100100"),
            .. Entity(30, "HERO_A", Me, "HAND", "HERO", "tag=ZONE_POSITION value=1"),
            .. Entity(31, "HERO_B", Me, "HAND", "HERO", "tag=ZONE_POSITION value=2"),
            .. Entity(32, "HERO_C", Me, "HAND", "HERO", "tag=ZONE_POSITION value=3"),
            .. Entity(33, "HERO_D", Me, "HAND", "HERO", "tag=ZONE_POSITION value=4"),
            "DebugPrintEntityChoices() - id=1 Player=Me#1234 TaskList=7 ChoiceType=MULLIGAN CountMin=1 CountMax=1",
            "DebugPrintEntityChoices() -   Source=GameEntity",
            "DebugPrintEntityChoices() -   Entities[0]=" + Hand(30, "HERO_A", 1),
            "DebugPrintEntityChoices() -   Entities[1]=" + Hand(31, "HERO_B", 2),
            "DebugPrintEntityChoices() -   Entities[2]=" + Hand(32, "HERO_C", 3),
            "DebugPrintEntityChoices() -   Entities[3]=" + Hand(33, "HERO_D", 4),
            // The player rerolls HERO_C into HERO_E, then picks HERO_E.
            "DebugPrintPower() - CHANGE_ENTITY - Updating Entity=" + Hand(32, "HERO_C", 3) + " CardID=HERO_E",
            "DebugPrintPower() -     tag=CARDTYPE value=HERO",
            "DebugPrintEntitiesChosen() - id=1 Player=Me#1234 EntitiesCount=1",
            "DebugPrintEntitiesChosen() -   Entities[0]=" + Hand(32, "HERO_E", 3),
            // After the pick the hero leaves the hand; later changes are not offers.
            "DebugPrintPower() - CHANGE_ENTITY - Updating Entity=" + Hand(31, "HERO_B", 2) + " CardID=HERO_F",
            "DebugPrintPower() -     tag=CARDTYPE value=HERO",
        ]);

        Assert.Equal(new[] { "HERO_A", "HERO_B", "HERO_C", "HERO_D", "HERO_E" }, facts.OfferedHeroes);

        // The picked hero in play, the game over: the row carries the offer and the anomaly.
        Feed(parser,
        [
            .. Entity(50, "TB_BaconShop_HERO_50", Me, "PLAY", "HERO", "tag=PLAYER_LEADERBOARD_PLACE value=3"),
            "DebugPrintPower() - TAG_CHANGE Entity=Me#1234 tag=HERO_ENTITY value=50 ",
            "DebugPrintPower() - TAG_CHANGE Entity=Me#1234 tag=PLAYSTATE value=LOST ",
        ]);
        var record = GameRecords.Build(parser.Game!, battlegrounds, Cards, null, "test", facts: facts);

        Assert.NotNull(record);
        Assert.Equal(3, record.Placement);
        Assert.Equal("HERO_A,HERO_B,HERO_C,HERO_D,HERO_E", record.OfferedHeroes);
        Assert.Equal("BG27_Anomaly_100", record.AnomalyCardId);
        Assert.Null(record.CombatResults);
        Assert.Null(record.WentFirst);
    }

    [Theory]
    [InlineData(Me, true)]
    [InlineData(Them, false)]
    public void Constructed_row_says_who_went_first(int first, bool expected)
    {
        var record = Ranked(first, new DeckDefinition("Mage", 42, "code", "FT_STANDARD", [637], new Dictionary<int, int>()));
        Assert.Equal(expected, record.WentFirst);
        Assert.Equal(42, record.DeckId);
        Assert.Equal("MAGE", record.PlayerClass);
    }

    [Fact]
    public void A_deck_of_another_class_is_not_taken_as_this_games_deck()
    {
        // A Priest deck still queued from an earlier game, while this game is played as Mage.
        var record = Ranked(Me, new DeckDefinition("Priest", 77, "code", "FT_STANDARD", [813], new Dictionary<int, int>()));
        Assert.Null(record.DeckId);
    }

    private static GameRecord Ranked(int firstPlayer, DeckDefinition deck)
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        var battlegrounds = new BattlegroundsTracker { Cards = Cards };
        parser.AddListener(battlegrounds);
        var firstEntity = firstPlayer == Me ? 2 : 3;
        Feed(parser,
        [
            .. Setup("GT_RANKED", 30, 40, opponentIsDummy: false),
            .. Entity(30, "HERO_08", Me, "PLAY", "HERO"),
            .. Entity(40, "HERO_09", Them, "PLAY", "HERO"),
            .. Entity(60, "CORE_CS2_029", Me, "HAND", "SPELL"),
            $"DebugPrintPower() - TAG_CHANGE Entity={firstEntity} tag=FIRST_PLAYER value=1 ",
            "DebugPrintPower() - TAG_CHANGE Entity=Me#1234 tag=PLAYSTATE value=WON ",
        ]);

        return GameRecords.Build(parser.Game!, battlegrounds, Cards, deck, "test")!;
    }

    [Fact]
    public void Broken_combat_json_reads_as_no_combats() =>
        Assert.Empty(BattlegroundsStats.Combats(new GameRecord { Key = "x", CombatResults = "[" }));

    [Fact]
    public void Backfill_fills_missing_fields_of_a_saved_game_and_keeps_saved_ones()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hp-fill-{Guid.NewGuid():N}.db");
        var store = new HistoryStore(path);
        var at = new DateTime(2026, 10, 20, 21, 0, 0);
        var saved = new GameRecord { Key = "g", StartedAt = at, EndedAt = at.AddMinutes(9), Mode = "Ranked", Result = "Win", DeckId = 5 };
        Assert.True(store.Add(saved));

        // Re-read from the log: who went first is new, the deck id there differs (kept as saved).
        Assert.True(store.FillMissingFields(saved with { WentFirst = false, DeckId = 9, CombatResults = null }));
        Assert.False(store.FillMissingFields(saved with { WentFirst = true }));

        var game = Assert.Single(store.Recent());
        Assert.False(game.WentFirst);
        Assert.Equal(5, game.DeckId);
        Assert.Null(game.CombatResults);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(path);
    }
}
