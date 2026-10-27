using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Stats;
using Hearthpulse.Core.Tracking;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// The deck page: per-game card facts read from Power.log lines (grammar of the October 2026 logs: TURN is already 1
/// during the mulligan, the opening hand is shown with SHOW_ENTITY, the coin is a CATA_COIN1 created in hand), the
/// history columns and family tables, deck families and merge suggestions, and the per-card aggregates.
/// </summary>
public sealed partial class DeckPageTests
{
    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream("""
        [
          {"id":"HERO_11","dbfId":78065,"name":"The Lich King","type":"HERO","cardClass":"DEATHKNIGHT"},
          {"id":"HERO_09","dbfId":813,"name":"Anduin","type":"HERO","cardClass":"PRIEST"},
          {"id":"CARD_A","dbfId":1001,"name":"Alpha","type":"SPELL","cost":1,"cardClass":"DEATHKNIGHT"},
          {"id":"CARD_B","dbfId":1002,"name":"Bravo","type":"MINION","cost":2,"cardClass":"DEATHKNIGHT"},
          {"id":"CARD_C","dbfId":1003,"name":"Charlie","type":"MINION","cost":3,"cardClass":"DEATHKNIGHT"},
          {"id":"CARD_D","dbfId":1004,"name":"Delta","type":"MINION","cost":4,"cardClass":"DEATHKNIGHT"},
          {"id":"CARD_E","dbfId":1005,"name":"Echo","type":"MINION","cost":5,"cardClass":"DEATHKNIGHT"},
          {"id":"CARD_F","dbfId":1006,"name":"Foxtrot","type":"SPELL","cost":2,"cardClass":"DEATHKNIGHT"},
          {"id":"CARD_G","dbfId":1007,"name":"Golf","type":"SPELL","cost":6,"cardClass":"DEATHKNIGHT"},
          {"id":"PRIEST_X","dbfId":2001,"name":"X","type":"SPELL","cardClass":"PRIEST"},
          {"id":"PRIEST_Y","dbfId":2002,"name":"Y","type":"SPELL","cardClass":"PRIEST"},
          {"id":"PRIEST_Z","dbfId":2003,"name":"Z","type":"MINION","cardClass":"PRIEST"},
          {"id":"PRIEST_W","dbfId":2004,"name":"W","type":"MINION","cardClass":"PRIEST"}
        ]
        """u8.ToArray()));

    private static readonly DateTime At = new(2026, 10, 23, 16, 0, 0);

    // ---- Power.log -> history row -------------------------------------------------------------

    private static string Ref(int id, string card, int player, string zone = "HAND") =>
        $"[entityName=E{id} id={id} zone={zone} zonePos=1 cardId={card} player={player}]";

    private static string[] Show(int id, string card, string type, int controller = 1) =>
    [
        $"DebugPrintPower() -     SHOW_ENTITY - Updating Entity={id} CardID={card}",
        $"DebugPrintPower() -         tag=CONTROLLER value={controller}",
        $"DebugPrintPower() -         tag=CARDTYPE value={type}",
        "DebugPrintPower() -         tag=ZONE value=HAND",
    ];

    private static string[] Play(int id, string card, int player) =>
    [
        $"DebugPrintPower() - BLOCK_START BlockType=PLAY Entity={Ref(id, card, player)} EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ",
        $"DebugPrintPower() -     TAG_CHANGE Entity={Ref(id, card, player)} tag=ZONE value=PLAY ",
        "DebugPrintPower() - BLOCK_END",
    ];

    private static string[] HiddenDeck(int from, int to, int controller) =>
        Enumerable.Range(from, to - from + 1).SelectMany(id => new[]
        {
            $"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID=",
            "DebugPrintPower() -     tag=ZONE value=DECK",
            $"DebugPrintPower() -     tag=CONTROLLER value={controller}",
        }).ToArray();

    /// <summary>
    /// A ranked game as the second player (Death Knight vs Priest): CARD_A..D offered with the coin, CARD_B sent back
    /// and CARD_E dealt instead; CARD_F drawn on turn 1; CARD_A played on turn 1, CARD_F on turn 2. The opponent plays
    /// three Priest cards from its deck and one it was given (created mid-game, so not a deck card). CARD_C transforms
    /// in hand and is played as CARD_G on turn 2; the opponent steals and plays CARD_D and plays a secret it never shows.
    /// </summary>
    private static string[] RankedGame() =>
    [
        "DebugPrintPower() - CREATE_GAME",
        "DebugPrintPower() -     GameEntity EntityID=1",
        "DebugPrintPower() -         tag=CARDTYPE value=GAME",
        "DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115198130930503 lo=11]",
        "DebugPrintPower() -         tag=CONTROLLER value=1",
        "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
        "DebugPrintPower() -         tag=HERO_ENTITY value=40",
        "DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=144115198130930503 lo=22]",
        "DebugPrintPower() -         tag=CONTROLLER value=2",
        "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
        "DebugPrintPower() -         tag=HERO_ENTITY value=41",
        .. HiddenDeck(4, 9, 1),
        .. HiddenDeck(20, 23, 2),
        "DebugPrintPower() - FULL_ENTITY - Creating ID=40 CardID=HERO_11",
        "DebugPrintPower() -     tag=ZONE value=PLAY",
        "DebugPrintPower() -     tag=CONTROLLER value=1",
        "DebugPrintPower() -     tag=CARDTYPE value=HERO",
        "DebugPrintPower() - FULL_ENTITY - Creating ID=41 CardID=HERO_09",
        "DebugPrintPower() -     tag=ZONE value=PLAY",
        "DebugPrintPower() -     tag=CONTROLLER value=2",
        "DebugPrintPower() -     tag=CARDTYPE value=HERO",
        "DebugPrintPower() - TAG_CHANGE Entity=3 tag=FIRST_PLAYER value=1 ",
        "DebugPrintPower() - TAG_CHANGE Entity=1 tag=TURN value=1 ",
        .. Show(4, "CARD_A", "SPELL"),
        .. Show(5, "CARD_B", "MINION"),
        .. Show(6, "CARD_C", "MINION"),
        .. Show(7, "CARD_D", "MINION"),
        "DebugPrintPower() - FULL_ENTITY - Creating ID=30 CardID=CATA_COIN1",
        "DebugPrintPower() -     tag=CONTROLLER value=1",
        "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
        "DebugPrintPower() -     tag=ZONE value=HAND",
        "DebugPrintGame() - GameType=GT_RANKED",
        "DebugPrintGame() - FormatType=FT_STANDARD",
        "DebugPrintGame() - PlayerID=1, PlayerName=Me#1",
        "DebugPrintGame() - PlayerID=2, PlayerName=You#2",
        "DebugPrintPower() - TAG_CHANGE Entity=Me#1 tag=MULLIGAN_STATE value=INPUT ",
        "DebugPrintPower() - TAG_CHANGE Entity=You#2 tag=MULLIGAN_STATE value=INPUT ",
        "DebugPrintEntityChoices() - id=1 Player=Me#1 TaskList= ChoiceType=MULLIGAN CountMin=0 CountMax=5",
        "DebugPrintEntityChoices() -   Source=GameEntity",
        // The client prints the options as they were before the reveal; the parser resolves them by id.
        "DebugPrintEntityChoices() -   Entities[0]=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=4 zone=DECK zonePos=0 cardId= player=1]",
        "DebugPrintEntityChoices() -   Entities[1]=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=5 zone=DECK zonePos=0 cardId= player=1]",
        "DebugPrintEntityChoices() -   Entities[2]=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=6 zone=DECK zonePos=0 cardId= player=1]",
        "DebugPrintEntityChoices() -   Entities[3]=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=7 zone=DECK zonePos=0 cardId= player=1]",
        "DebugPrintEntityChoices() -   Entities[4]=" + Ref(30, "CATA_COIN1", 1),
        "DebugPrintEntitiesChosen() - id=1 Player=Me#1 EntitiesCount=1",
        "DebugPrintEntitiesChosen() -   Entities[0]=" + Ref(5, "CARD_B", 1),
        "DebugPrintPower() - TAG_CHANGE Entity=You#2 tag=MULLIGAN_STATE value=DEALING ",
        "DebugPrintPower() - TAG_CHANGE Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=23 zone=DECK zonePos=0 cardId= player=2] tag=ZONE value=HAND ",
        "DebugPrintPower() - TAG_CHANGE Entity=You#2 tag=MULLIGAN_STATE value=DONE ",
        "DebugPrintPower() - TAG_CHANGE Entity=Me#1 tag=MULLIGAN_STATE value=DEALING ",
        .. Show(8, "CARD_E", "MINION"),
        "DebugPrintPower() - TAG_CHANGE Entity=" + Ref(5, "CARD_B", 1) + " tag=ZONE value=DECK ",
        "DebugPrintPower() - TAG_CHANGE Entity=Me#1 tag=MULLIGAN_STATE value=DONE ",
        // Opponent's turn 1: a deck card.
        "DebugPrintPower() - SHOW_ENTITY - Updating Entity=20 CardID=PRIEST_X",
        "DebugPrintPower() -     tag=CONTROLLER value=2",
        "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
        "DebugPrintPower() -     tag=ZONE value=HAND",
        .. Play(20, "PRIEST_X", 2),
        // Our turn 1: draw CARD_F, play CARD_A.
        "DebugPrintPower() - TAG_CHANGE Entity=1 tag=TURN value=2 ",
        .. Show(9, "CARD_F", "SPELL"),
        .. Play(4, "CARD_A", 1),
        // A kept card turns into another one in hand: it still came from the deck as CARD_C.
        "DebugPrintPower() - CHANGE_ENTITY - Updating Entity=" + Ref(6, "CARD_C", 1) + " CardID=CARD_G",
        "DebugPrintPower() -     tag=CREATOR value=4",
        // Opponent's turn 2: two deck cards and a card it was given.
        "DebugPrintPower() - TAG_CHANGE Entity=1 tag=TURN value=3 ",
        "DebugPrintPower() - SHOW_ENTITY - Updating Entity=21 CardID=PRIEST_Y",
        "DebugPrintPower() -     tag=CONTROLLER value=2",
        "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
        .. Play(21, "PRIEST_Y", 2),
        "DebugPrintPower() - SHOW_ENTITY - Updating Entity=22 CardID=PRIEST_Z",
        "DebugPrintPower() -     tag=CONTROLLER value=2",
        "DebugPrintPower() -     tag=CARDTYPE value=MINION",
        .. Play(22, "PRIEST_Z", 2),
        "DebugPrintPower() - FULL_ENTITY - Creating ID=50 CardID=PRIEST_W",
        "DebugPrintPower() -     tag=ZONE value=HAND",
        "DebugPrintPower() -     tag=CONTROLLER value=2",
        "DebugPrintPower() -     tag=CARDTYPE value=MINION",
        "DebugPrintPower() -     tag=CREATOR value=22",
        .. Play(50, "PRIEST_W", 2),
        // The opponent takes CARD_D from our hand and plays it, and plays a secret it never reveals.
        "DebugPrintPower() - TAG_CHANGE Entity=" + Ref(7, "CARD_D", 1) + " tag=CONTROLLER value=2 ",
        .. Play(7, "CARD_D", 2),
        "DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=23 zone=HAND zonePos=1 cardId= player=2] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ",
        "DebugPrintPower() -     TAG_CHANGE Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=23 zone=HAND zonePos=1 cardId= player=2] tag=ZONE value=SECRET ",
        "DebugPrintPower() - BLOCK_END",
        // Our turn 2: play the transformed CARD_C (now CARD_G), then CARD_F.
        "DebugPrintPower() - TAG_CHANGE Entity=1 tag=TURN value=4 ",
        .. Play(6, "CARD_G", 1),
        .. Play(9, "CARD_F", 1),
        "DebugPrintPower() - TAG_CHANGE Entity=Me#1 tag=PLAYSTATE value=WON ",
        "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE ",
    ];

    private static (GameRecord Record, GameState Game) ParseRanked(ConstructedMeta? meta = null, IEnumerable<string>? lines = null)
    {
        var parser = new PowerParser(DateOnly.FromDateTime(At));
        var battlegrounds = new BattlegroundsTracker { Cards = Cards };
        var facts = new GameFactsTracker();
        GameRecord? record = null;
        parser.AddListener(battlegrounds);
        parser.AddListener(facts);
        parser.AddListener(new OnEnd(game => record = GameRecords.Build(game, battlegrounds, Cards, null, "test", facts: facts, meta: meta)));
        foreach (var body in lines ?? RankedGame())
        {
            parser.Feed("D 16:09:35.0000000 GameState." + body);
        }

        parser.Flush();
        Assert.NotNull(record);
        return (record, parser.Game!);
    }

    private sealed class OnEnd(Action<GameState> ended) : IPowerListener
    {
        public void OnGameEnded(GameState game) => ended(game);
    }

    [Fact]
    public void Mulligan_draws_and_plays_are_read_from_power_log_lines()
    {
        var (record, game) = ParseRanked();

        var mulligan = GameCards.Mulligan(record.Mulligan);
        Assert.NotNull(mulligan);
        Assert.Equal(["CARD_A", "CARD_B", "CARD_C", "CARD_D"], mulligan.Offered);
        Assert.Equal(["CARD_B"], mulligan.Replaced);
        Assert.Equal(["CARD_E"], mulligan.Received);
        Assert.Equal(["CARD_A", "CARD_C", "CARD_D"], mulligan.Kept);
        Assert.True(game.Entities[5].Mulliganed);
        Assert.False(game.Entities[4].Mulliganed);

        // The opening hand after the mulligan is turn 0; the turn-1 draw comes after it. CARD_B went back: not drawn.
        Assert.Equal(
            [new CardTurn("CARD_A", 0), new CardTurn("CARD_C", 0), new CardTurn("CARD_D", 0), new CardTurn("CARD_E", 0), new CardTurn("CARD_F", 1)],
            GameCards.Turns(record.CardsDrawn));
        // The transformed card counts as the deck card it was drawn as; the stolen CARD_D is not ours to play.
        Assert.Equal([new CardTurn("CARD_A", 1), new CardTurn("CARD_C", 2), new CardTurn("CARD_F", 2)], GameCards.Turns(record.CardsPlayed));

        // Only the opponent's own deck cards it played face up: not the card it was given, not our stolen card, not the
        // secret it never revealed.
        Assert.Equal([new CardTurn("PRIEST_X", 1), new CardTurn("PRIEST_Y", 2), new CardTurn("PRIEST_Z", 2)], GameCards.Turns(record.OpponentCards));
        Assert.False(record.WentFirst);
        Assert.Equal("PRIEST", record.OpponentClass);
        Assert.Null(record.OpponentArchetype);
    }

    [Fact]
    public void A_game_picked_up_without_its_mulligan_saves_no_card_data()
    {
        // As after a reconnect: the log has no mulligan choice, so the opening hand and early draws are unknown.
        var (record, _) = ParseRanked(lines: RankedGame().Where(l => !l.StartsWith("DebugPrintEntityChoices", StringComparison.Ordinal)));
        Assert.Null(record.Mulligan);
        Assert.Null(record.CardsDrawn);
        Assert.Null(record.CardsPlayed);
        Assert.NotNull(record.OpponentCards);
    }

    private static MetaArchetype Archetype(int id, string name, string heroClass, params string[] core) =>
        new(id, name.ToLowerInvariant(), name, heroClass, 1000, 520, 0.52, 0.05, 0, 0, 0, 0, core, []);

    private static ConstructedMeta Meta(params MetaArchetype[] archetypes) =>
        new("standard", "all", "past-7", DateTime.UtcNow, 10000, archetypes, []);

    [Fact]
    public void The_opponent_archetype_is_saved_only_when_its_cards_clearly_point_to_one()
    {
        // Three of the seen cards are core cards of Quest Priest only.
        var clear = Meta(Archetype(1, "Quest Priest", "PRIEST", "PRIEST_X", "PRIEST_Y", "PRIEST_Z"), Archetype(2, "Control Priest", "PRIEST", "PRIEST_W"));
        Assert.Equal("Quest Priest", ParseRanked(clear).Record.OpponentArchetype);

        // Both archetypes hold the cards: no call, the opponent is just a Priest.
        var unclear = Meta(Archetype(1, "Quest Priest", "PRIEST", "PRIEST_X", "PRIEST_Y", "PRIEST_Z"), Archetype(2, "Control Priest", "PRIEST", "PRIEST_X", "PRIEST_Y"));
        Assert.Null(ParseRanked(unclear).Record.OpponentArchetype);

        // A Wild meta does not place a Standard game.
        var wild = new ConstructedMeta("wild", "all", "past-7", DateTime.UtcNow, 10000, [Archetype(1, "Quest Priest", "PRIEST", "PRIEST_X", "PRIEST_Y", "PRIEST_Z")], []);
        Assert.Null(ParseRanked(wild).Record.OpponentArchetype);
    }

    // ---- history file ----------------------------------------------------------------------

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"hp-deck-{Guid.NewGuid():N}.db");

    private static void Drop(string path)
    {
        SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    [Fact]
    public void An_old_history_file_gets_the_card_columns_and_family_tables()
    {
        var path = TempDb();
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // The schema of the My Stats branch: games with its added columns, no card columns, no family tables.
            command.CommandText = """
                CREATE TABLE games (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, game_key TEXT NOT NULL UNIQUE, started_at TEXT NOT NULL,
                    ended_at TEXT NOT NULL, mode TEXT NOT NULL, game_type TEXT NOT NULL, format TEXT NOT NULL,
                    player_name TEXT NOT NULL, opponent_name TEXT NOT NULL, hero TEXT NOT NULL, opponent_hero TEXT NOT NULL,
                    player_class TEXT NOT NULL, opponent_class TEXT NOT NULL, result TEXT NOT NULL, placement INTEGER NOT NULL,
                    rating_before INTEGER NULL, rating_after INTEGER NULL, turns INTEGER NOT NULL, deck_name TEXT NULL,
                    deck_code TEXT NULL, tavern_tier INTEGER NOT NULL, final_board TEXT NULL, races TEXT NULL, rank TEXT NULL,
                    source TEXT NOT NULL, combat_results TEXT NULL, offered_heroes TEXT NULL, anomaly TEXT NULL,
                    rank_star_level INTEGER NULL, rank_legend INTEGER NULL, went_first INTEGER NULL, deck_id INTEGER NULL);
                INSERT INTO games (game_key, started_at, ended_at, mode, game_type, format, player_name, opponent_name, hero,
                    opponent_hero, player_class, opponent_class, result, placement, turns, deck_name, deck_code, tavern_tier,
                    source, went_first, deck_id)
                VALUES ('old', '2026-10-22T10:00:00.0000000', '2026-10-22T10:09:00.0000000', 'Ranked', 'GT_RANKED', 'FT_STANDARD',
                    'Me#1', 'You#2', 'HERO_11', 'HERO_09', 'DEATHKNIGHT', 'PRIEST', 'Win', 0, 8, 'DK', 'code', 0, 'live', 1, 7);
                """;
            command.ExecuteNonQuery();
        }

        var store = new HistoryStore(path);
        var old = Assert.Single(store.Recent());
        Assert.Null(old.Mulligan);
        Assert.Null(old.CardsDrawn);
        Assert.Null(old.CardsPlayed);
        Assert.Null(old.OpponentCards);
        Assert.Null(old.OpponentArchetype);
        Assert.True(old.WentFirst);
        Assert.Empty(store.DeckFamilies());

        // A re-read of the same game (backfill) fills the card columns and keeps what was saved.
        var reread = ParseRanked().Record with { Key = "old", WentFirst = false };
        Assert.True(store.FillMissingFields(reread));
        Assert.Null(Assert.Single(store.Recent()).Mulligan);
        var filled = Assert.Single(store.WithCards(store.Recent()));
        Assert.True(filled.WentFirst);
        Assert.Equal(reread.Mulligan, filled.Mulligan);
        Assert.Equal(reread.CardsDrawn, filled.CardsDrawn);
        Assert.Equal(reread.OpponentCards, filled.OpponentCards);
        Assert.False(store.FillMissingFields(reread));

        // Opening again is a no-op.
        store = new HistoryStore(path);
        Assert.Single(store.Recent());
        Drop(path);
    }

    [Fact]
    public void Families_merge_split_rename_and_remember_dismissed_pairs()
    {
        var path = TempDb();
        var store = new HistoryStore(path);
        var changes = 0;
        store.DeckFamiliesChanged += () => changes++;

        var id = store.MergeDecks(["v1", "v2"], "Rune DK");
        var family = Assert.Single(store.DeckFamilies());
        Assert.Equal(id, family.Id);
        Assert.Equal("Rune DK", family.Name);
        Assert.Equal("v1", family.MainKey);
        Assert.Equal(["v1", "v2"], family.Members);

        // Merging a third version into a member's family keeps that family; a second family folds into it.
        var other = store.MergeDecks(["v4", "v5"]);
        Assert.NotEqual(id, other);
        Assert.Equal(id, store.MergeDecks(["v3", "v2", "v4"]));
        family = Assert.Single(store.DeckFamilies());
        Assert.Equal(["v1", "v2", "v3", "v4", "v5"], family.Members.OrderBy(k => k, StringComparer.Ordinal));

        store.SetDeckFamilyMain(id, "v3");
        store.SetDeckFamilyMain(id, "not-a-member");
        store.RenameDeckFamily(id, "  ");
        family = Assert.Single(store.DeckFamilies());
        Assert.Equal("v3", family.MainKey);
        Assert.Null(family.Name);

        // A split version is on its own again and is not suggested with the rest any more.
        store.SplitDeck("v3");
        family = Assert.Single(store.DeckFamilies());
        Assert.DoesNotContain("v3", family.Members);
        Assert.Equal("v1", family.MainKey);
        var dismissed = store.DismissedDeckPairs();
        Assert.Contains(HistoryStore.DeckPair("v3", "v1"), dismissed);
        Assert.Contains(HistoryStore.DeckPair("v5", "v3"), dismissed);

        // Merging again overrules the split.
        store.MergeDecks(["v3", "v1"]);
        Assert.DoesNotContain(HistoryStore.DeckPair("v3", "v1"), store.DismissedDeckPairs());

        // A family left with one version is dissolved.
        var pair = store.MergeDecks(["x1", "x2"]);
        store.SplitDeck("x1");
        Assert.DoesNotContain(store.DeckFamilies(), f => f.Id == pair);
        Assert.True(changes >= 8);
        Drop(path);
    }

    // ---- families and suggestions -----------------------------------------------------------

    /// <summary>A Standard deck code of Death Knight cards (dbfId, copies) for the tests.</summary>
    private static string Code(params (int Dbf, int Copies)[] cards) => CodeFor(78065, cards);

    /// <summary>A Standard deck code with a given hero (a hero skin has its own dbfId).</summary>
    private static string CodeFor(int hero, params (int Dbf, int Copies)[] cards)
    {
        var bytes = new List<byte>();
        void Varint(int value)
        {
            do
            {
                var b = (byte)(value & 0x7F);
                value >>= 7;
                bytes.Add(value > 0 ? (byte)(b | 0x80) : b);
            }
            while (value > 0);
        }

        Varint(0);
        Varint(1);
        Varint(2);
        Varint(1);
        Varint(hero);
        foreach (var copies in new[] { 1, 2 })
        {
            var group = cards.Where(c => c.Copies == copies).ToList();
            Varint(group.Count);
            group.ForEach(c => Varint(c.Dbf));
        }

        Varint(0);
        return Convert.ToBase64String(bytes.ToArray());
    }

    /// <summary>A 30-card list: 15 cards of two copies, ids from <paramref name="first"/>.</summary>
    private static (int, int)[] Deck(int first, params int[] swaps) =>
        Enumerable.Range(first, 15).Select(d => swaps.Contains(d) ? (d + 500, 2) : (d, 2)).ToArray();

    private static GameRecord Game(string key, string deck, string code, string result, int minutesFromStart, long? deckId = null,
        string opponent = "PRIEST", string format = "FT_STANDARD", string playerClass = "DEATHKNIGHT") => new()
    {
        Key = key,
        StartedAt = At.AddMinutes(minutesFromStart),
        EndedAt = At.AddMinutes(minutesFromStart + 8),
        Mode = "Ranked",
        Format = format,
        PlayerClass = playerClass,
        OpponentClass = opponent,
        Result = result,
        Turns = 8,
        DeckName = deck,
        DeckCode = code,
        DeckId = deckId,
    };

    [Fact]
    public void Two_versions_of_one_deck_are_suggested_and_their_stats_add_up_once_merged()
    {
        // "Рыцарь смерти2" and "Рыцарь смерти3": the player's two Death Knight lists, 28 of 30 cards in common.
        var v2Code = Code(Deck(1000));
        var v3Code = Code(Deck(1000, 1014));
        var v2 = DeckRecords.CardListKey(v2Code);
        var v3 = DeckRecords.CardListKey(v3Code);
        var other = Code(Deck(3000));
        var games = new List<GameRecord>
        {
            Game("g1", "Рыцарь смерти2", v2Code, "Win", 0, 11),
            Game("g2", "Рыцарь смерти2", v2Code, "Loss", 10, 11),
            Game("g3", "Рыцарь смерти3", v3Code, "Win", 20, 12),
            Game("g4", "Рыцарь смерти3", v3Code, "Win", 30, 12),
            Game("g5", "Совсем другая", other, "Loss", 40, 13),
            Game("g6", "Рыцарь смерти Wild", v3Code, "Win", 50, 12, format: "FT_WILD"),
        };
        games[^1] = games[^1] with { DeckCode = Code(Deck(1000, 1013)) };

        var versions = DeckFamilies.Versions(games);
        Assert.Equal(4, versions.Count);
        var families = DeckFamilies.Build(versions, []);
        Assert.Equal(4, families.Count);
        Assert.Equal(28.0 / 30, DeckFamilies.Overlap(versions.Single(v => v.Key == v2), versions.Single(v => v.Key == v3)), 3);
        Assert.True(DeckFamilies.SimilarNames("Рыцарь смерти2", "Рыцарь смерти 3"));
        Assert.False(DeckFamilies.SimilarNames("Quest Priest", "Control Warrior"));

        var suggestion = Assert.Single(DeckFamilies.Suggest(families, new HashSet<(string, string)>()));
        Assert.Equal(new[] { v2, v3 }.Order(StringComparer.Ordinal), new[] { suggestion.First.Key, suggestion.Second.Key }.Order(StringComparer.Ordinal));
        Assert.True(suggestion.SimilarNames);

        // Dismissed: not suggested again.
        Assert.Empty(DeckFamilies.Suggest(families, new HashSet<(string, string)> { HistoryStore.DeckPair(v3, v2) }));

        // Merged (v2 as main): one deck with all four games, both versions and the card difference between them.
        var merged = DeckFamilies.Build(versions, [new DeckFamilyRecord(7, null, v2, [v2, v3])]);
        var family = merged.Single(f => f.Id == 7);
        Assert.Equal("family:7", family.Key);
        // Shown with the space Hearthstone leaves out before the copy number.
        Assert.Equal("Рыцарь смерти 2", family.Name);
        Assert.Equal(new WinLoss(4, 3, 1), family.Record);
        Assert.Equal([v2, v3], family.Versions.Select(v => v.Key));
        Assert.Equal([new CardDelta(1514, 2), new CardDelta(1014, -2)], DeckFamilies.Diff(family.Versions[0], family.Versions[1]));
        Assert.Equal(3, merged.Count);
        Assert.Empty(DeckFamilies.Suggest(merged, new HashSet<(string, string)>()));
    }

    [Fact]
    public void A_low_overlap_needs_a_name_or_deck_id_hint()
    {
        // 22 of 30 cards in common (73%): suggested only with a hint.
        var a = Code(Deck(1000));
        var b = Code(Deck(1000, 1010, 1011, 1012, 1013));
        var plain = DeckFamilies.Build(DeckFamilies.Versions([Game("1", "Alpha", a, "Win", 0), Game("2", "Bravo", b, "Win", 5)]), []);
        Assert.Empty(DeckFamilies.Suggest(plain, new HashSet<(string, string)>()));

        var sameId = DeckFamilies.Build(DeckFamilies.Versions([Game("1", "Alpha", a, "Win", 0, 5), Game("2", "Bravo", b, "Win", 5, 5)]), []);
        Assert.True(Assert.Single(DeckFamilies.Suggest(sameId, new HashSet<(string, string)>())).SameDeckId);

        // Alike names, but another class.
        var otherClass = DeckFamilies.Build(DeckFamilies.Versions([Game("1", "Alpha", a, "Win", 0), Game("2", "Alpha", b, "Win", 5, playerClass: "MAGE")]), []);
        Assert.Empty(DeckFamilies.Suggest(otherClass, new HashSet<(string, string)>()));
    }

    // ---- deck page aggregates ---------------------------------------------------------------

    private static GameRecord CardGame(string key, string result, bool first, string opponent, string mulligan, string drawn, string played) =>
        Game(key, "DK", Code(Deck(1000)), result, int.Parse(key[1..])) with
        {
            WentFirst = first,
            OpponentClass = opponent,
            Mulligan = mulligan,
            CardsDrawn = drawn,
            CardsPlayed = played,
        };

    [Fact]
    public void Mulligan_and_card_stats_count_games_per_card()
    {
        var deck = Code((1001, 2), (1002, 2), (1003, 1));
        GameRecord G(string key, string result, string mulligan, string drawn, string played) =>
            CardGame(key, result, key != "g3", "PRIEST", mulligan, drawn, played) with { DeckCode = deck };

        var games = new List<GameRecord>
        {
            // Both copies of A offered, one sent back: kept. B sent back: replaced. A played turn 1 and 3.
            G("g1", "Win", """{"offered":["CARD_A","CARD_A","CARD_B"],"replaced":["CARD_A","CARD_B"],"received":["CARD_C","CARD_C"]}""",
                """[{"id":"CARD_A","turn":0},{"id":"CARD_C","turn":0},{"id":"CARD_C","turn":0},{"id":"CARD_B","turn":4}]""",
                """[{"id":"CARD_A","turn":3},{"id":"CARD_A","turn":1}]"""),
            // A kept and played on 2; lost.
            G("g2", "Loss", """{"offered":["CARD_A","CARD_C","CARD_B"],"replaced":[],"received":[]}""",
                """[{"id":"CARD_A","turn":0},{"id":"CARD_C","turn":0},{"id":"CARD_B","turn":0}]""",
                """[{"id":"CARD_A","turn":2}]"""),
            // A sent back; not drawn again. Won.
            G("g3", "Win", """{"offered":["CARD_A","CARD_B","CARD_C","CARD_C"],"replaced":["CARD_A"],"received":["CARD_B"]}""",
                """[{"id":"CARD_B","turn":0},{"id":"CARD_B","turn":0},{"id":"CARD_C","turn":0},{"id":"CARD_C","turn":0}]""",
                "[]"),
            // An old game without card data counts for the summary only.
            G("g4", "Loss", null!, null!, null!),
        };

        var family = DeckFamilies.Build(DeckFamilies.Versions(games), []).Single();
        var stats = DeckDetails.Cards(family, family.Games, Cards).ToDictionary(c => c.CardId);
        Assert.Equal(3, DeckDetails.CardDataGames(family.Games));

        var a = stats["CARD_A"];
        Assert.Equal(2, a.Copies);
        Assert.Equal(3, a.Offered);
        Assert.Equal(new WinLoss(2, 1, 1), a.WhenKept);
        Assert.Equal(new WinLoss(1, 1, 0), a.WhenReplaced);
        Assert.Equal(2.0 / 3, a.KeepRate!.Value, 3);
        Assert.Equal(new WinLoss(2, 1, 1), a.WhenDrawn);
        Assert.Equal(new WinLoss(2, 1, 1), a.WhenPlayed);
        Assert.Equal(1.5, a.AveragePlayTurn); // first play per game: 1 and 2

        var b = stats["CARD_B"];
        Assert.Equal(new WinLoss(1, 1, 0), b.WhenReplaced);
        Assert.Equal(new WinLoss(3, 2, 1), b.WhenDrawn);
        Assert.Null(b.AveragePlayTurn);

        // Card order: by cost (A 1, B 2, C 3).
        Assert.Equal(["CARD_A", "CARD_B", "CARD_C"], DeckDetails.Cards(family, family.Games, Cards).Select(c => c.CardId));

        var summary = DeckDetails.Summary(family.Games);
        Assert.Equal(new WinLoss(4, 2, 2), summary.Record);
        Assert.Equal(new WinLoss(3, 1, 2), summary.OnPlay);
        Assert.Equal(new WinLoss(1, 1, 0), summary.OnCoin);
        Assert.Equal(TimeSpan.FromMinutes(8), summary.AverageDuration);

        Assert.Equal([1.0, 0.5, 2.0 / 3, 0.5], DeckDetails.WinRateTimeline(family.Games).Select(p => p.WinRate));
    }

    [Fact]
    public void Matchups_split_by_archetype_where_it_was_told_and_filter_the_games()
    {
        var meta = Meta(Archetype(1, "Quest Priest", "PRIEST", "PRIEST_X", "PRIEST_Y", "PRIEST_Z"));
        var games = new List<GameRecord>
        {
            CardGame("g1", "Win", true, "PRIEST", null!, null!, null!) with { OpponentArchetype = "Control Priest" },
            // Saved without an archetype, but its saved cards place it now.
            CardGame("g2", "Loss", true, "PRIEST", null!, null!, null!) with
            {
                OpponentCards = """[{"id":"PRIEST_X","turn":1},{"id":"PRIEST_Y","turn":2},{"id":"PRIEST_Z","turn":3}]""",
            },
            CardGame("g3", "Win", true, "PRIEST", null!, null!, null!),
            CardGame("g4", "Loss", true, "MAGE", null!, null!, null!),
        };

        var classes = DeckDetails.ByOpponentClass(games);
        Assert.Equal(new WinLoss(3, 2, 1), classes.Single(m => m.Key == "PRIEST").Record);
        var archetypes = DeckDetails.ByOpponentArchetype(games, meta);
        Assert.Equal(["PRIEST|Control Priest", "PRIEST|Quest Priest"], archetypes.Select(m => m.Key).Order(StringComparer.Ordinal));

        Assert.Equal(3, DeckDetails.Filter(games, "PRIEST", meta).Count);
        Assert.Equal("g2", Assert.Single(DeckDetails.Filter(games, "PRIEST|Quest Priest", meta)).Key);
        Assert.Equal(4, DeckDetails.Filter(games, null, meta).Count);
    }

    // ---- one deck, everywhere (review M1, M2) ----------------------------------------------

    [Fact]
    public void A_family_survives_its_games_getting_their_deck_code()
    {
        var path = TempDb();
        var store = new HistoryStore(path);
        var code = Code(Deck(1000));
        var other = Code(Deck(1000, 1014));
        // Games saved with the deck's name only, as before Decks.log was read; and a version with a code.
        Assert.True(store.Add(Game("a1", "Рыцарь смерти2", null!, "Win", 0)));
        Assert.True(store.Add(Game("a2", "Рыцарь смерти2", null!, "Loss", 10)));
        Assert.True(store.Add(Game("b1", "Рыцарь смерти3", other, "Win", 20)));
        var nameKey = DeckRecords.NameKey("Рыцарь смерти2", "DEATHKNIGHT");
        var id = store.MergeDecks([nameKey, DeckRecords.CardListKey(other)], "Blood DK", nameKey);
        store.DismissDeckPair(nameKey, "somebody-else");

        // Decks.log gives one of the games its code: that game is now the card-list version.
        Assert.Equal(1, store.FillDecks([new DeckFill("a1", "Рыцарь смерти2", code)]));

        var families = DeckFamilies.Build(DeckFamilies.Versions(store.Recent()), store.DeckFamilies());
        var family = Assert.Single(families);
        Assert.Equal(id, family.Id);
        Assert.Equal("Blood DK", family.Name);
        Assert.Equal(3, family.Games.Count);
        Assert.Contains(family.Versions, v => v.Key == DeckRecords.CardListKey(code));
        Assert.Contains(HistoryStore.DeckPair(DeckRecords.CardListKey(code), "somebody-else"), store.DismissedDeckPairs());
        Drop(path);
    }

    [Fact]
    public void A_list_with_another_hero_skin_is_the_same_version()
    {
        var list = Deck(1000);
        var games = new[]
        {
            Game("1", "DK", CodeFor(78065, list), "Win", 0),
            Game("2", "DK", CodeFor(99999, list), "Loss", 10),
        };
        var version = Assert.Single(DeckFamilies.Versions(games));
        Assert.Equal(new WinLoss(2, 1, 1), version.Record);
        Assert.Empty(DeckFamilies.Suggest(DeckFamilies.Build([version], []), new HashSet<(string, string)>()));
    }

    [Fact]
    public void The_in_game_deck_record_counts_the_merged_versions()
    {
        var v2 = Code(Deck(1000));
        var v3 = Code(Deck(1000, 1014));
        var games = new[]
        {
            Game("g1", "DK2", v2, "Win", 0),
            Game("g2", "DK3", v3, "Loss", 10),
            Game("g3", "Mage", Code(Deck(3000)), "Win", 20),
        };
        var matched = DeckRecords.Matching(games, "DK2", v2, "DEATHKNIGHT");
        Assert.Single(matched);
        var stored = new[] { new DeckFamilyRecord(1, null, null, [DeckRecords.CardListKey(v2), DeckRecords.CardListKey(v3)]) };
        Assert.Equal(["g1", "g2"], DeckFamilies.WithFamily(games, matched, stored).Select(g => g.Key));
        Assert.Same(matched, DeckFamilies.WithFamily(games, matched, []));
    }

    [Fact]
    public void Merging_into_the_open_deck_keeps_its_name()
    {
        var path = TempDb();
        var store = new HistoryStore(path);
        var older = store.MergeDecks(["a", "b"], "Older");
        var newer = store.MergeDecks(["c", "d"], "Mine");
        Assert.Equal(newer, store.MergeDecks(["c", "d", "a", "b"], into: newer));
        var family = Assert.Single(store.DeckFamilies());
        Assert.Equal("Mine", family.Name);
        Assert.DoesNotContain(store.DeckFamilies(), f => f.Id == older);
        Drop(path);
    }

    [Fact]
    public void A_current_meta_names_the_opponent_over_the_saved_archetype()
    {
        var meta = Meta(Archetype(1, "Quest Priest", "PRIEST", "PRIEST_X", "PRIEST_Y", "PRIEST_Z"));
        var game = CardGame("g1", "Win", true, "PRIEST", null!, null!, null!) with
        {
            OpponentArchetype = "Old Name Priest",
            OpponentCards = """[{"id":"PRIEST_X","turn":1},{"id":"PRIEST_Y","turn":2},{"id":"PRIEST_Z","turn":3}]""",
        };
        Assert.Equal("Quest Priest", DeckDetails.OpponentArchetype(game, meta));
        Assert.Equal("Old Name Priest", DeckDetails.OpponentArchetype(game, null));
    }

    [Fact]
    public void Families_saved_with_raw_deck_codes_are_rekeyed_on_open()
    {
        var path = TempDb();
        var v2 = Code(Deck(1000));
        var v3 = Code(Deck(1000, 1014));
        var store = new HistoryStore(path);
        var id = store.MergeDecks([v2, v3], "Blood DK", v2);
        store.DismissDeckPair(v2, "x");

        store = new HistoryStore(path);
        var family = Assert.Single(store.DeckFamilies());
        Assert.Equal(id, family.Id);
        Assert.Equal(new[] { DeckRecords.CardListKey(v2), DeckRecords.CardListKey(v3) }.Order(StringComparer.Ordinal), family.Members.Order(StringComparer.Ordinal));
        Assert.Equal(DeckRecords.CardListKey(v2), family.MainKey);
        Assert.Equal([HistoryStore.DeckPair(DeckRecords.CardListKey(v2), "x")], store.DismissedDeckPairs());
        Drop(path);
    }
}
