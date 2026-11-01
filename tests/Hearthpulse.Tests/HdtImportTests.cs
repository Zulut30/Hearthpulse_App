using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Migration;
using Hearthpulse.Core.Replay;
using Xunit.Abstractions;

namespace Hearthpulse.Tests;

/// <summary>
/// Moving over from Hearthstone Deck Tracker: synthetic DeckStats / DefaultDeckStats / PlayerDecks / BgsLastGames files
/// shaped as HDT writes them (no personal data), imported into a History of their own.
/// </summary>
public sealed class HdtImportTests : IDisposable
{
    private const string Deck = "11111111-1111-1111-1111-111111111111";
    private const string ArenaDeck = "22222222-2222-2222-2222-222222222222";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-hdt-{Guid.NewGuid():N}");
    private readonly string _hdt;
    private readonly HistoryStore _history;
    private readonly ReplayStore _replays;

    internal static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes("""
        [
          {"id": "HERO_08", "dbfId": 637, "name": "Jaina Proudmoore", "type": "HERO", "cardClass": "MAGE"},
          {"id": "HERO_01", "dbfId": 7, "name": "Garrosh Hellscream", "type": "HERO", "cardClass": "WARRIOR"},
          {"id": "CS2_029", "dbfId": 315, "name": "Fireball", "type": "SPELL", "cardClass": "MAGE", "cost": 4},
          {"id": "CS2_032", "dbfId": 1004, "name": "Flamestrike", "type": "SPELL", "cardClass": "MAGE", "cost": 7},
          {"id": "EX1_279", "dbfId": 1087, "name": "Pyroblast", "type": "SPELL", "cardClass": "MAGE", "cost": 10},
          {"id": "BG21_001", "dbfId": 70001, "name": "Some Minion", "type": "MINION"},
          {"id": "BG_ANOMALY_1", "dbfId": 90001, "name": "Some Anomaly", "type": "BATTLEGROUND_ANOMALY"}
        ]
        """)));

    private readonly ITestOutputHelper _output;

    public HdtImportTests(ITestOutputHelper output)
    {
        _output = output;
        _hdt = Path.Combine(_dir, "HearthstoneDeckTracker");
        Directory.CreateDirectory(_dir);
        _history = new HistoryStore(Path.Combine(_dir, "history.db"));
        _replays = new ReplayStore(Path.Combine(_dir, "replays"));
        WriteFixture(_hdt);
    }

    /// <summary>The synthetic HDT folder: 3 finished games (2 Constructed, 1 Battlegrounds), a clone, an unfinished and a Mercenaries game, 1 library deck.</summary>
    internal static void WriteFixture(string hdt)
    {
        Directory.CreateDirectory(Path.Combine(hdt, "Replays"));
        File.WriteAllText(Path.Combine(hdt, "PlayerDecks.xml"), PlayerDecks);
        File.WriteAllText(Path.Combine(hdt, "DeckStats.xml"), DeckStats(
            Game("aaaaaaaa-0000-0000-0000-000000000001", "2026-10-20T18:00:00.1234567+03:00", "Win", version: "1", coin: false),
            Game("aaaaaaaa-0000-0000-0000-000000000002", "2026-10-21T18:00:00+03:00", "Loss", version: "2", coin: true),
            // A clone of the first game, an unfinished game, a Mercenaries game: never imported.
            Game("aaaaaaaa-0000-0000-0000-000000000003", "2026-10-20T18:00:00.1234567+03:00", "Win", extra: "<IsClone>true</IsClone>"),
            Game("aaaaaaaa-0000-0000-0000-000000000004", "2026-10-22T18:00:00+03:00", "None"),
            Game("aaaaaaaa-0000-0000-0000-000000000005", "2026-10-22T19:00:00+03:00", "Win", mode: "Mercenaries", type: "GT_MERCENARIES_PVP")));
        File.WriteAllText(Path.Combine(hdt, "DefaultDeckStats.xml"), DefaultDeckStats);
        File.WriteAllText(Path.Combine(hdt, "BgsLastGames.xml"), BgsLastGames);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static string Game(string id, string start, string result, string version = "1", bool coin = false, string mode = "Ranked",
        string type = "GT_RANKED", string extra = "") => $"""
        <Game>
          <GameId>{id}</GameId>
          <DeckId>{Deck}</DeckId>
          <DeckName>Big Spell Mage</DeckName>
          <PlayerDeckVersion><Major>{version}</Major><Minor>0</Minor><Revision>0</Revision><Build>0</Build></PlayerDeckVersion>
          <StartTime>{start}</StartTime>
          <EndTime>{start.Replace("T18:00:00", "T18:09:30", StringComparison.Ordinal)}</EndTime>
          <Result>{result}</Result>
          <WasConceded>false</WasConceded>
          <Coin>{(coin ? "true" : "false")}</Coin>
          <Turns>17</Turns>
          <GameMode>{mode}</GameMode>
          <GameType>{type}</GameType>
          <Format>Standard</Format>
          <PlayerName>Player#1000</PlayerName>
          <OpponentName>Rival#2000</OpponentName>
          <PlayerHero>Mage</PlayerHero>
          <OpponentHero>Warrior</OpponentHero>
          <PlayerHeroCardId>HERO_08</PlayerHeroCardId>
          <OpponentHeroCardId>HERO_01</OpponentHeroCardId>
          <LeagueId>5</LeagueId>
          <StarLevel>44</StarLevel>
          <OpponentCards><Card Id="CS2_106" Count="2" /></OpponentCards>
          <ServerInfo><Address>10.0.0.1</Address><SpectatorPassword>secret</SpectatorPassword></ServerInfo>
          {extra}
        </Game>
        """;

    private static string DeckStats(params string[] games) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <DeckStatsList xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <DeckStats>
            <Deck>
              <DeckId>{Deck}</DeckId>
              <Games>{string.Concat(games)}</Games>
              <Name>Big Spell Mage</Name>
            </Deck>
          </DeckStats>
        </DeckStatsList>
        """;

    private const string PlayerDecks = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Decks xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <Tags><Tag>All</Tag><Tag>Favorite</Tag><Tag>None</Tag><Tag>Ladder</Tag></Tags>
          <Deck>
            <DeckId>{Deck}</DeckId>
            <Name>Big Spell Mage</Name>
            <Class>Mage</Class>
            <Cards>
              <Card><Id>CS2_029</Id><Count>2</Count></Card>
              <Card><Id>EX1_279</Id><Count>1</Count></Card>
            </Cards>
            <Tags><Tag>Favorite</Tag><Tag>Ladder</Tag></Tags>
            <Note>Mulligan for Fireball</Note>
            <LastEdited>2026-10-21T10:00:00+03:00</LastEdited>
            <Archived>true</Archived>
            <Version><Major>2</Major><Minor>0</Minor><Revision>0</Revision><Build>0</Build></Version>
            <DeckHistory>
              <Deck>
                <DeckId>33333333-3333-3333-3333-333333333333</DeckId>
                <Name>Big Spell Mage</Name>
                <Class>Mage</Class>
                <Cards><Card><Id>CS2_029</Id><Count>2</Count></Card><Card><Id>CS2_032</Id><Count>2</Count></Card></Cards>
                <Version><Major>1</Major><Minor>0</Minor><Revision>0</Revision><Build>0</Build></Version>
              </Deck>
            </DeckHistory>
            <HsId>123</HsId>
          </Deck>
          <Deck>
            <DeckId>{ArenaDeck}</DeckId>
            <Name>Arena 10-20 18:00</Name>
            <Class>Mage</Class>
            <IsArenaDeck>true</IsArenaDeck>
          </Deck>
        </Decks>
        """;

    private const string DefaultDeckStats = """
        <?xml version="1.0" encoding="utf-8"?>
        <DefaultDeckStats xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <DeckStats>
            <DeckStats>
              <DeckId>00000000-0000-0000-0000-000000000000</DeckId>
              <Games>
                <Game>
                  <GameId>bbbbbbbb-0000-0000-0000-000000000001</GameId>
                  <DeckId>00000000-0000-0000-0000-000000000000</DeckId>
                  <DeckName>none</DeckName>
                  <StartTime>2026-10-23T20:00:00+03:00</StartTime>
                  <EndTime>2026-10-23T20:20:00+03:00</EndTime>
                  <Result>Loss</Result>
                  <Turns>23</Turns>
                  <GameMode>Battlegrounds</GameMode>
                  <GameType>GT_BATTLEGROUNDS</GameType>
                  <PlayerName>Player#1000</PlayerName>
                  <OpponentName>Some Hero</OpponentName>
                  <PlayerHero>BaconPHhero</PlayerHero>
                  <PlayerHeroCardId>TB_BaconShop_HERO_50</PlayerHeroCardId>
                  <BattlegroundsRating>6100</BattlegroundsRating>
                  <BattlegroundsDetails><FinalPlacement>3</FinalPlacement><AnomalyDbfId>90001</AnomalyDbfId></BattlegroundsDetails>
                </Game>
              </Games>
              <Name>BaconPHhero</Name>
            </DeckStats>
          </DeckStats>
        </DefaultDeckStats>
        """;

    private const string BgsLastGames = """
        <?xml version="1.0" encoding="utf-8"?>
        <BgsLastGames>
          <Game Player="1_2" StartTime="2026-10-23T20:00:00+03:00" EndTime="2026-10-23T20:20:00+03:00" Hero="TB_BaconShop_HERO_50"
                Rating="6100" RatingAfter="6132" Placemenent="3" FriendlyGame="false" Duos="false">
            <FinalBoard><Minion><CardId>BG21_001</CardId><Tags Tag="47" Value="5" /><Tags Tag="45" Value="9" /><Tags Tag="44" Value="2" /><Tags Tag="12" Value="1" /></Minion></FinalBoard>
          </Game>
        </BgsLastGames>
        """;

    private MigrationResult Import() => HdtImport.Import(_hdt, _history, _replays, Cards, Path.Combine(_dir, "work"));

    [Fact]
    public void Scan_counts_the_finished_games_and_the_library_decks()
    {
        var found = HdtImport.Scan(_hdt)!;

        Assert.Equal(MigrationSource.Hdt, found.Source);
        Assert.Equal(3, found.Games);
        Assert.Equal(1, found.Decks);
        Assert.Null(HdtImport.Scan(Path.Combine(_dir, "nothing")));
    }

    [Fact]
    public void Constructed_games_come_with_their_deck_version_rank_and_result()
    {
        var result = Import();

        Assert.Null(result.Error);
        Assert.Equal(3, result.Added);
        // The clone, the unfinished and the Mercenaries game.
        Assert.Equal(3, result.Skipped);
        var games = _history.Recent(10).Where(g => !g.IsBattlegrounds).OrderBy(g => g.StartedAt).ToList();
        Assert.Equal(2, games.Count);
        var first = games[0];
        Assert.Equal("hdt", first.Source);
        Assert.Equal($"{new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.FromHours(3)).LocalDateTime:yyyyMMddHHmmss}|Player#1000|GT_RANKED", first.Key);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.FromHours(3)).LocalDateTime, first.StartedAt, TimeSpan.FromSeconds(1));
        Assert.InRange(first.Duration.TotalSeconds, 569, 571);
        Assert.Equal("Ranked", first.Mode);
        Assert.Equal("FT_STANDARD", first.Format);
        Assert.Equal(("HERO_08", "MAGE", "HERO_01", "WARRIOR"), (first.HeroCardId, first.PlayerClass, first.OpponentHeroCardId, first.OpponentClass));
        Assert.Equal("Win", first.Result);
        // HDT counts the player's own turns already.
        Assert.Equal(17, first.Turns);
        Assert.Equal("[{\"id\":\"CS2_106\",\"turn\":0},{\"id\":\"CS2_106\",\"turn\":0}]", _history.WithCards([first])[0].OpponentCards);
        Assert.True(first.WentFirst);
        Assert.False(games[1].WentFirst);
        Assert.Equal("Diamond 7", first.Rank);
        Assert.Equal(44, first.RankStarLevel);
        Assert.Equal("Big Spell Mage", first.DeckName);
        // Version 1 had Flamestrikes, version 2 has a Pyroblast: each game keeps the list it played.
        Assert.Equal(new Dictionary<int, int> { [315] = 2, [1004] = 2 }, DeckString.TryDecode(first.DeckCode!)!.Cards);
        Assert.Equal(new Dictionary<int, int> { [315] = 2, [1087] = 1 }, DeckString.TryDecode(games[1].DeckCode!)!.Cards);
    }

    [Fact]
    public void A_battlegrounds_game_takes_hero_rating_and_board_from_the_bg_file()
    {
        Import();

        var bg = Assert.Single(_history.Recent(10), g => g.IsBattlegrounds);
        Assert.Equal("TB_BaconShop_HERO_50", bg.HeroCardId);
        Assert.Equal(3, bg.Placement);
        Assert.Equal("Win", bg.Result);
        Assert.Equal((6100, 6132), (bg.RatingBefore, bg.RatingAfter));
        Assert.Equal(23, bg.Turns);
        Assert.Equal("BG_ANOMALY_1", bg.AnomalyCardId);
        Assert.Contains("\"id\":\"BG21_001\",\"atk\":5,\"hp\":7,\"gold\":true", bg.FinalBoard, StringComparison.Ordinal);
    }

    [Fact]
    public void Importing_again_or_a_game_already_in_history_adds_nothing()
    {
        // The first game, recorded from the logs 40 seconds later than HDT's start.
        _history.Add(new GameRecord
        {
            Key = $"{new DateTimeOffset(2026, 10, 20, 18, 0, 40, TimeSpan.FromHours(3)).LocalDateTime:yyyyMMddHHmmss}|Player#1000|GT_RANKED",
            StartedAt = new DateTimeOffset(2026, 10, 20, 18, 0, 40, TimeSpan.FromHours(3)).LocalDateTime,
            GameType = "GT_RANKED",
            Mode = "Ranked",
            OpponentClass = "WARRIOR",
            Result = "Win",
        });

        var first = Import();
        var again = Import();

        Assert.Equal((2, 1), (first.Added, first.Already));
        Assert.Equal((0, 3), (again.Added, again.Already));
        Assert.Equal(3, _history.GameCount());
    }

    [Fact]
    public void Library_decks_are_saved_and_the_played_versions_are_one_family()
    {
        var result = Import();

        Assert.Equal(1, result.Decks);
        var deck = Assert.Single(_history.SavedDecks());
        Assert.Equal(("hdt", Deck, "Big Spell Mage", "MAGE"), (deck.Source, deck.ExternalId, deck.Name, deck.PlayerClass));
        Assert.Equal(["Ladder"], deck.Tags);
        Assert.Equal("Mulligan for Fireball", deck.Note);
        Assert.True(deck.Archived);
        Assert.Equal("FT_STANDARD", deck.Format);
        Assert.Equal(new Dictionary<int, int> { [315] = 2, [1087] = 1 }, DeckString.TryDecode(deck.DeckCode!)!.Cards);
        var family = Assert.Single(_history.DeckFamilies());
        Assert.Equal("Big Spell Mage", family.Name);
        Assert.Equal(2, family.Members.Count);
    }

    [Fact]
    public void Server_info_and_names_of_the_files_never_reach_history()
    {
        Import();

        Assert.All(_history.Recent(10), g => Assert.DoesNotContain("secret", g.ToString(), StringComparison.Ordinal));
        Assert.All(_history.Recent(10), g => Assert.DoesNotContain("10.0.0.1", g.ToString(), StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_dir, "work")) && Directory.EnumerateFileSystemEntries(Path.Combine(_dir, "work")).Any());
        // Nothing is written into HDT's folder.
        Assert.Equal(["BgsLastGames.xml", "DeckStats.xml", "DefaultDeckStats.xml", "PlayerDecks.xml", "Replays"],
            Directory.EnumerateFileSystemEntries(_hdt).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_game_with_an_hdt_replay_is_rebuilt_from_its_log_with_a_match_replay()
    {
        string[] bodies =
        [
            .. ReplayRecorderTests.Setup,
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=PLAYSTATE value=WON ",
            "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE ",
        ];
        var replay = Path.Combine(_hdt, "Replays", "game.hdtreplay");
        using (var zip = ZipFile.Open(replay, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("output_log.txt").Open()))
        {
            // The log's times are the PC's clock, as HDT's start is (+03:00 in the file).
            var clock = new DateTimeOffset(2026, 10, 20, 18, 0, 5, TimeSpan.FromHours(3)).LocalDateTime;
            writer.Write(string.Join('\n', bodies.Select(b => $"D {clock:HH:mm:ss}.0000000 GameState." + b)));
        }

        File.WriteAllText(Path.Combine(_hdt, "DeckStats.xml"), DeckStats(
            Game("aaaaaaaa-0000-0000-0000-000000000001", "2026-10-20T18:00:00+03:00", "Win", extra: "<ReplayFile>game.hdtreplay</ReplayFile>")));
        File.Delete(Path.Combine(_hdt, "DefaultDeckStats.xml"));

        var result = Import();

        Assert.Equal((1, 1), (result.Added, result.Replays));
        var game = Assert.Single(_history.Recent(10));
        Assert.Equal("hdt", game.Source);
        Assert.Equal("Big Spell Mage", game.DeckName);
        Assert.NotNull(game.Replay);
        Assert.NotNull(_replays.Load(game.Replay));
    }

    [Fact]
    public void A_damaged_file_falls_back_to_its_bak_copy_and_otherwise_stops_the_import()
    {
        var stats = Path.Combine(_hdt, "DeckStats.xml");
        File.Copy(stats, stats + ".bak");
        File.WriteAllText(stats, "<DeckStatsList><DeckStats><Deck>");

        Assert.Equal(3, Import().Added);

        File.WriteAllText(stats + ".bak", "not xml");
        Assert.Equal(MigrationErrors.Damaged, HdtImport.Import(_hdt, new HistoryStore(Path.Combine(_dir, "other.db")), _replays, Cards, Path.Combine(_dir, "work")).Error);
    }

    [Fact]
    public void A_file_with_a_dtd_is_not_expanded()
    {
        File.WriteAllText(Path.Combine(_hdt, "PlayerDecks.xml"),
            "<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY x SYSTEM \"file:///C:/Windows/win.ini\">]><Decks><Deck><Name>&x;</Name></Deck></Decks>");

        Assert.Equal(MigrationErrors.Damaged, Import().Error);
    }

    [Fact]
    public void A_game_the_logs_record_later_replaces_the_transferred_one()
    {
        Import();
        var started = new DateTimeOffset(2026, 10, 20, 18, 0, 40, TimeSpan.FromHours(3)).LocalDateTime;

        // The same game, imported from Hearthstone's own logs 40 seconds after HDT's start.
        Assert.True(_history.Add(new GameRecord
        {
            Key = $"{started:yyyyMMddHHmmss}|Player#1000|GT_RANKED",
            StartedAt = started,
            EndedAt = started.AddMinutes(9),
            GameType = "GT_RANKED",
            Mode = "Ranked",
            OpponentClass = "WARRIOR",
            Result = "Win",
            Source = "backfill",
        }));

        var ranked = _history.Recent(10).Where(g => g.GameType == "GT_RANKED").OrderBy(g => g.StartedAt).ToList();
        Assert.Equal(2, ranked.Count);
        Assert.Equal("backfill", ranked[0].Source);
        Assert.Equal("hdt", ranked[1].Source);
    }

    [Fact]
    public void The_same_game_twice_in_hdt_keeps_its_longer_record()
    {
        // A reconnect HDT saved as two games: 55 seconds lost, then the whole game won.
        File.WriteAllText(Path.Combine(_hdt, "DeckStats.xml"), DeckStats(
            Game("aaaaaaaa-0000-0000-0000-000000000011", "2026-10-20T18:00:00+03:00", "Loss").Replace("T18:09:30", "T18:00:55", StringComparison.Ordinal),
            Game("aaaaaaaa-0000-0000-0000-000000000012", "2026-10-20T18:01:30+03:00", "Win").Replace("T18:01:30+03:00</EndTime>", "T18:10:30+03:00</EndTime>", StringComparison.Ordinal)));
        File.Delete(Path.Combine(_hdt, "DefaultDeckStats.xml"));

        var result = Import();

        Assert.Equal((1, 1), (result.Added, result.Already));
        var game = Assert.Single(_history.Recent(10));
        Assert.Equal("Win", game.Result);
    }

    [Fact]
    public void Transferred_games_are_never_sent_to_the_profile_or_the_stats_pool()
    {
        Import();
        _history.Add(new GameRecord { Key = "20261024120000|Player#1000|GT_RANKED", StartedAt = new DateTime(2026, 10, 24, 12, 0, 0), GameType = "GT_RANKED", Mode = "Ranked", Result = "Win" });

        var upload = _history.PageForUpload(0, 100);

        Assert.Equal(["live"], upload.Select(g => g.Source));
        var bg = _history.Recent(10).Single(g => g.IsBattlegrounds);
        Assert.Null(Core.Pool.PoolGame.FromHistory(bg, "secret", id => id));
    }

    [Fact]
    public void A_broken_bgs_file_leaves_battlegrounds_games_without_their_board_only()
    {
        File.WriteAllText(Path.Combine(_hdt, "BgsLastGames.xml"), "<broken");

        var result = Import();

        Assert.Equal((3, null), (result.Added, result.Error));
        Assert.Null(_history.Recent(10).Single(g => g.IsBattlegrounds).FinalBoard);
    }

    /// <summary>The owner's own HDT folder, read only: every game reads, and how long it takes.</summary>
    [HdtDataFact]
    public void The_kept_hdt_data_imports_whole()
    {
        var history = new HistoryStore(Path.Combine(_dir, "real.db"));
        var found = HdtImport.Scan(HdtImport.DefaultFolder)!;
        var clock = Stopwatch.StartNew();

        var result = HdtImport.Import(HdtImport.DefaultFolder, history, new ReplayStore(Path.Combine(_dir, "real-replays")), CardDb.Empty, Path.Combine(_dir, "work"));

        _output.WriteLine($"{found.Games} games, {found.Decks} decks: added {result.Added}, already {result.Already}, skipped {result.Skipped}, replays {result.Replays}, decks {result.Decks} in {clock.Elapsed.TotalSeconds:N1} s");
        Assert.Null(result.Error);
        Assert.True(result.Added + result.Already <= found.Games);
        Assert.True(result.Added > 0);
    }
}

/// <summary>Runs only where Hearthstone Deck Tracker's data folder exists (the owner's PC).</summary>
public sealed class HdtDataFactAttribute : FactAttribute
{
    public HdtDataFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(HdtImport.DefaultFolder, "DeckStats.xml")))
        {
            Skip = "No Hearthstone Deck Tracker data on this machine.";
        }
    }
}
