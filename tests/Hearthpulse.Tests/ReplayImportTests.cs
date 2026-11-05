using System.IO.Compression;
using System.Text;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Tests;

/// <summary>
/// The import filter (<c>docs/replay-library.md</c>): a foreign replay is read with size limits, refused whole when a
/// line breaks its grammar, cleaned of what the player could not see, and stored rebuilt from its known fields without
/// ranks and ratings. A Power.log goes to Imported only.
/// </summary>
public sealed class ReplayImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-import-{Guid.NewGuid():N}");

    public ReplayImportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    internal static readonly string[] ConstructedEvents =
    [
        "T 1 L 0",
        "E 1 L PLAY 0 HERO_08 HERO HEALTH=30",
        "E 2 O PLAY 0 HERO_01 HERO HEALTH=30",
        "D L 26",
        "E 3 L HAND 1 CS2_029 SPELL COST=4",
        "E 4 O HAND 1 - - ORIGIN=start",
        "V L RESOURCES 1",
        "T 2 O 31",
        "E 5 O PLAY 1 CS2_182 MINION ATK=4 HEALTH=5 COST=4",
        "A 5 1",
        "B",
        "E 3 L PLAY 1 CS2_029 SPELL COST=4",
        "P 3 5",
        "C GENERAL - CS2_032,CS2_024,EX1_279",
        "c CS2_032",
        "V L PLAYSTATE WON",
        "R WON",
    ];

    internal static readonly string[] PostGame = ["N 4 64", "K 64 CS2_182 3 played 0"];

    internal static ReplaySummary ConstructedSummary => new()
    {
        Mode = "Ranked",
        GameType = "GT_RANKED",
        Format = "FT_STANDARD",
        StartedAt = new DateTime(2026, 10, 28, 18, 41, 7, DateTimeKind.Utc),
        Seconds = 420,
        HeroCardId = "HERO_08",
        PlayerClass = "MAGE",
        OpponentHeroCardId = "HERO_01",
        OpponentClass = "WARRIOR",
        Result = "Win",
        Turns = 9,
        WentFirst = true,
        OpponentArchetype = "Control Warrior",
        RatingBefore = 3100,
        RatingAfter = 3125,
    };

    /// <summary>A Constructed replay as the app writes it, with both ranks in the header.</summary>
    internal static string Constructed(IEnumerable<string>? events = null, ReplaySummary? summary = null, string? header = null)
    {
        var list = (events ?? ConstructedEvents).ToList();
        var text = ReplayFile.Write(
            new ReplayHeader { LocalRank = "Legend 1,204", OpponentRank = "Diamond 3", Summary = summary ?? ConstructedSummary, Events = list.Count },
            list.Concat(PostGame));
        return header is null ? text : $"HPR4\n{header}\n{string.Join('\n', list.Concat(PostGame))}\n";
    }

    private static string Clean(string text)
    {
        var clean = ReplayImport.Sanitize(text, CardDb.Empty, out var error);
        Assert.Equal(ReplayImportError.None, error);
        return clean!;
    }

    private static ReplayImportError Refusal(string text)
    {
        Assert.Null(ReplayImport.Sanitize(text, CardDb.Empty, out var error));
        return error;
    }

    [Fact]
    public void An_own_replay_keeps_every_line_and_loses_ranks_and_rating()
    {
        var (header, body) = ReplayFile.Read(Clean(Constructed()))!.Value;

        Assert.Equal(4, header.Version);
        Assert.Equal([.. ConstructedEvents, .. PostGame], body);
        Assert.Equal(ConstructedEvents.Length, header.Events);
        Assert.Null(header.LocalRank);
        Assert.Null(header.OpponentRank);
        Assert.Null(header.Summary!.RatingBefore);
        Assert.Null(header.Summary.RatingAfter);
        Assert.Equal("HERO_08", header.Summary.HeroCardId);
        Assert.Equal("Control Warrior", header.Summary.OpponentArchetype);
        Assert.Equal(new DateTime(2026, 10, 28, 18, 41, 7), header.Summary.StartedAt);
        Assert.True(MatchReplay.Parse(Clean(Constructed()))!.HasPostGame);
    }

    [Theory]
    [InlineData("hello\nworld", ReplayImportError.NotReplay)]
    [InlineData("", ReplayImportError.NotReplay)]
    [InlineData("HPR1\n{}\nE 1 L DECK 0 CS2_029 SPELL", ReplayImportError.Version)]
    [InlineData("HPR9\n{}\nT 1 L 0", ReplayImportError.Version)]
    [InlineData("HPR4\n{not json\nT 1 L 0", ReplayImportError.Damaged)]
    [InlineData("HPR4\n{\"kind\":\"mercenaries\"}\nT 1 L 0", ReplayImportError.Damaged)]
    [InlineData("HPR4", ReplayImportError.Damaged)]
    public void A_bad_header_is_refused(string text, ReplayImportError error) => Assert.Equal(error, Refusal(text));

    [Theory]
    [InlineData("E 9 L PLAY 2 https://evil.example/x MINION ATK=1")]
    [InlineData("E 9 L PLAY 2 ..\\..\\boot.ini MINION")]
    [InlineData("E 9 L PLAY 2 CS2_182 MINION PLAYER_NAME=1")]
    [InlineData("E 9 L DECK 2 CS2_182 MINION")]
    [InlineData("E 9 L PLAY 2 CS2_182 MINION ATK=4 name=Rival#4321")]
    [InlineData("Q Rival#4321")]
    [InlineData("T 1 L 0 extra")]
    [InlineData("C GENERAL - Rival#4321")]
    [InlineData("c CS2_032 ")]
    [InlineData("R WON\tx")]
    public void A_foreign_line_refuses_the_whole_file(string line)
    {
        Assert.Equal(ReplayImportError.Damaged, Refusal(Constructed([.. ConstructedEvents.Take(5), line, .. ConstructedEvents.Skip(5)])));
    }

    [Fact]
    public void A_post_game_line_out_of_place_or_malformed_is_refused()
    {
        // An N line before an event line: events must come first.
        Assert.Equal(ReplayImportError.Damaged, Refusal($"HPR4\n{{\"kind\":\"constructed\",\"events\":2}}\nT 1 L 0\nN 4 64\nT 2 O 5\n"));
        Assert.Equal(ReplayImportError.Damaged, Refusal($"HPR4\n{{\"kind\":\"constructed\",\"events\":1}}\nT 1 L 0\nK 64 CS2_182 3 stolen 0\n"));
    }

    [Fact]
    public void Extra_header_and_summary_fields_are_dropped()
    {
        var text = Constructed(header: "{\"kind\":\"constructed\",\"playerName\":\"Me#1234\",\"localRank\":\"Legend 7\","
            + "\"summary\":{\"mode\":\"Ranked\",\"heroCardId\":\"HERO_08\",\"battleTag\":\"Rival#4321\",\"accountId\":\"144115198130930503\","
            + "\"ratingBefore\":9000,\"opponentArchetype\":\"see https://evil.example\",\"deckCode\":\"not a deck\"},\"events\":17}");

        var clean = Clean(text);

        Assert.DoesNotContain("Me#1234", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("Rival#4321", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("144115198130930503", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("Legend", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("9000", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("not a deck", clean, StringComparison.Ordinal);
        Assert.Equal("HERO_08", ReplayFile.Read(clean)!.Value.Header.Summary!.HeroCardId);
    }

    [Fact]
    public void A_card_id_in_the_opponents_hand_or_secret_becomes_a_card_back()
    {
        string[] events =
        [
            "T 1 L 0",
            "E 4 O HAND 1 CS2_182 MINION ATK=4 HEALTH=5",
            "V 4 ATK 5",
            "E 6 O SECRET 1 EX1_130 SPELL COST=2",
            "E 7 O HAND 2 - - ORIGIN=coin",
            "E 4 O PLAY 1 CS2_182 MINION ATK=4 HEALTH=5",
            "V 4 ATK 6",
        ];

        var body = ReplayFile.Read(Clean(Constructed(events)))!.Value.Body;

        Assert.Equal(
            ["T 1 L 0", "E 4 O HAND 1 - -", "E 6 O SECRET 1 - -", "E 7 O HAND 2 - - ORIGIN=coin", "E 4 O PLAY 1 CS2_182 MINION ATK=4 HEALTH=5", "V 4 ATK 6", .. PostGame],
            body);
    }

    [Fact]
    public void An_opponents_quest_stays_face_up_when_the_cards_say_it_is_one()
    {
        var cards = CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes("""[{"id": "UNG_940", "dbfId": 41499, "type": "SPELL", "mechanics": ["QUEST"]}]""")));
        var text = Constructed(["T 1 L 0", "E 6 O SECRET 1 UNG_940 SPELL"]);

        Assert.Contains("E 6 O SECRET 1 UNG_940 SPELL", ReplayImport.Sanitize(text, cards, out _), StringComparison.Ordinal);
        Assert.Contains("E 6 O SECRET 1 - -", ReplayImport.Sanitize(text, CardDb.Empty, out _), StringComparison.Ordinal);
    }

    [Fact]
    public void An_old_file_is_stored_as_format_4_with_a_summary_from_its_events()
    {
        var text = "HPR2\n{\"kind\":\"constructed\",\"localRank\":\"Gold 4\",\"events\":17}\n" + string.Join('\n', ConstructedEvents) + "\n";

        var (header, body) = ReplayFile.Read(Clean(text))!.Value;

        Assert.Equal(4, header.Version);
        Assert.Null(header.LocalRank);
        Assert.Equal(ConstructedEvents, body);
        Assert.Equal("HERO_08", header.Summary!.HeroCardId);
        Assert.Equal("HERO_01", header.Summary.OpponentHeroCardId);
        Assert.Equal("Win", header.Summary.Result);
        // The file does not say when it was played: no date, not the day of the import.
        Assert.Equal(DateTime.MinValue, header.Summary.StartedAt);
        Assert.StartsWith("game-", ReplayImport.FileNameFor(Clean(text), header.Summary), StringComparison.Ordinal);
        Assert.False(MatchReplay.Parse(Clean(text))!.HasPostGame);
    }

    [Fact]
    public void Battlegrounds_steps_lose_Bobs_offer_in_combat_and_the_opponent_outside_it()
    {
        string[] events =
        [
            "C HERO - TB_BaconShop_HERO_50,TB_BaconShop_HERO_08",
            "c TB_BaconShop_HERO_50",
            "T 1 30",
            "E 1 L PLAY 0 TB_BaconShop_HERO_50 HERO HEALTH=30",
            "E 2 B PLAY 1 BG_CS2_065 MINION ATK=2 HEALTH=3",
            "E 3 B PLAY 2 BGS_004 MINION ATK=2 HEALTH=2",
            "E 9 O PLAY 1 BGS_061 MINION ATK=9 HEALTH=9",
            "M BUY 2",
            "E 2 L HAND 0 BG_CS2_065 MINION ATK=2 HEALTH=3",
            "V L RESOURCES 3",
            "F 1 75",
            "E 4 B PLAY 1 BGS_039 MINION ATK=1 HEALTH=1",
            "E 5 O PLAY 0 TB_BaconShop_HERO_08 HERO HEALTH=30",
            "E 6 O PLAY 1 BGS_004 MINION ATK=2 HEALTH=2",
            "V 4 ATK 3",
            "M ATTACK 6 -",
            "f WIN 0 3",
            "L 1 L TB_BaconShop_HERO_50 0 30 0 1",
            "T 2 90",
            "V 6 ATK 5",
            "E 7 B PLAY 1 BGS_039 MINION ATK=1 HEALTH=1",
            "R 4",
        ];
        var data = new BattlegroundsReplayData { Lobby = [new BattlegroundsLobbyHero("TB_BaconShop_HERO_50", 4, true)] };
        var text = ReplayFile.WriteBattlegrounds(new ReplayHeader { Summary = new ReplaySummary { Mode = "Battlegrounds", HeroCardId = "TB_BaconShop_HERO_50", Placement = 4, RatingBefore = 6100 } },
            data, events);

        var (header, body) = ReplayFile.Read(Clean(text))!.Value;

        Assert.Equal(
            [
                "C HERO - TB_BaconShop_HERO_50,TB_BaconShop_HERO_08",
                "c TB_BaconShop_HERO_50",
                "T 1 30",
                "E 1 L PLAY 0 TB_BaconShop_HERO_50 HERO HEALTH=30",
                "E 2 B PLAY 1 BG_CS2_065 MINION ATK=2 HEALTH=3",
                "E 3 B PLAY 2 BGS_004 MINION ATK=2 HEALTH=2",
                "M BUY 2",
                "E 2 L HAND 0 BG_CS2_065 MINION ATK=2 HEALTH=3",
                "V L RESOURCES 3",
                "F 1 75",
                "X 3",
                "E 5 O PLAY 0 TB_BaconShop_HERO_08 HERO HEALTH=30",
                "E 6 O PLAY 1 BGS_004 MINION ATK=2 HEALTH=2",
                "M ATTACK 6 -",
                "f WIN 0 3",
                "L 1 L TB_BaconShop_HERO_50 0 30 0 1",
                "X 5",
                "X 6",
                "T 2 90",
                "E 7 B PLAY 1 BGS_039 MINION ATK=1 HEALTH=1",
                "R 4",
            ],
            body.Take(header.Events));
        Assert.Null(header.Summary!.RatingBefore);
        Assert.StartsWith(ReplayFile.DataLine, body[^1], StringComparison.Ordinal);
        Assert.Equal(4, ReplayFile.ReadBattlegrounds(body)!.Lobby[0].Place);
    }

    [Theory]
    [InlineData("E 9 T PLAY 1 BGS_004 MINION")]
    [InlineData("E 9 O HAND 1 BGS_004 MINION")]
    [InlineData("L 1 L Rival#4321 1 30 0 1")]
    public void A_battlegrounds_line_outside_the_grammar_refuses_the_file(string line)
    {
        var text = ReplayFile.WriteBattlegrounds(new ReplayHeader { Summary = new ReplaySummary { Mode = "Battlegrounds" } }, new BattlegroundsReplayData(), ["T 1 30", line]);

        Assert.Equal(ReplayImportError.Damaged, Refusal(text));
    }

    /// <summary>
    /// T6 (mutation survivors): a bad card id on a board, in the lobby or in the final board, or a round result outside
    /// Win/Loss/Tie, refuses the whole file, wherever it sits.
    /// </summary>
    public static TheoryData<string> BadBattlegrounds => new() { "board", "opponent board", "lobby", "result", "final board" };

    [Theory]
    [MemberData(nameof(BadBattlegrounds))]
    public void A_bad_minion_hero_or_result_in_battlegrounds_data_refuses_the_file(string where)
    {
        ReplayMinion[] bad = [new("BG_GOOD_1", 2, 3, false), new("../x", 1, 1, false)];
        var round = new BattlegroundsRound
        {
            Turn = 1,
            Board = where == "board" ? bad : [],
            OpponentBoard = where == "opponent board" ? bad : [],
            Result = where == "result" ? "Pwned" : "Win",
        };
        var data = new BattlegroundsReplayData
        {
            Rounds = [round],
            Lobby = [new("BG_HERO_1", 1, true), new(where == "lobby" ? "C:\\x" : "BG_HERO_2", 2, false)],
        };
        var summary = new ReplaySummary
        {
            Mode = "Battlegrounds",
            FinalBoard = where == "final board" ? [new("../x", 1, 1, false, [])] : [new("BG_GOOD_1", 1, 1, false, [])],
        };

        Assert.Equal(ReplayImportError.Damaged, Refusal(ReplayFile.WriteBattlegrounds(new ReplayHeader { Summary = summary }, data, ["T 1 30"])));
    }

    /// <summary>The control of the theory above: the same pieces, all good, pass.</summary>
    [Fact]
    public void Good_minions_heroes_and_results_in_battlegrounds_data_pass()
    {
        ReplayMinion[] good = [new("BG_GOOD_1", 2, 3, false)];
        var data = new BattlegroundsReplayData
        {
            Rounds = [new BattlegroundsRound { Turn = 1, Board = good, OpponentBoard = good, Result = "Win" }],
            Lobby = [new("BG_HERO_1", 1, true), new("BG_HERO_2", 2, false)],
        };
        var summary = new ReplaySummary { Mode = "Battlegrounds", FinalBoard = [new("BG_GOOD_1", 1, 1, false, [])] };

        var clean = ReplayFile.Read(Clean(ReplayFile.WriteBattlegrounds(new ReplayHeader { Summary = summary }, data, ["T 1 30"])))!.Value;

        Assert.Equal("BG_GOOD_1", Assert.Single(clean.Header.Summary!.FinalBoard).CardId);
    }

    /// <summary>T6: summary words that are not plain words, and a result outside the known ones, are not kept.</summary>
    [Fact]
    public void Summary_words_that_are_not_words_are_dropped()
    {
        var text = ReplayFile.WriteBattlegrounds(new ReplayHeader
        {
            Summary = new ReplaySummary { Mode = "Battlegrounds", GameType = "GT x; see evil.example", PlayerClass = "<b>MAGE</b>", Result = "Pwned" },
        }, new BattlegroundsReplayData(), ["T 1 30"]);

        var summary = ReplayFile.Read(Clean(text))!.Value.Header.Summary!;

        Assert.Equal((string.Empty, string.Empty, "Unknown"), (summary.GameType, summary.PlayerClass, summary.Result));
    }

    [Fact]
    public void A_bad_card_id_in_the_rounds_refuses_the_file()
    {
        var data = new BattlegroundsReplayData { Rounds = [new BattlegroundsRound { Turn = 1, OpponentHero = "file:///C:/x" }] };
        var text = ReplayFile.WriteBattlegrounds(new ReplayHeader { Summary = new ReplaySummary { Mode = "Battlegrounds" } }, data, ["T 1 30"]);

        Assert.Equal(ReplayImportError.Damaged, Refusal(text));
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void A_card_id_is_at_most_64_characters(int length, bool kept)
    {
        var card = "BG_" + new string('A', length - 3);
        var data = new BattlegroundsReplayData { Rounds = [new BattlegroundsRound { Turn = 1, OpponentHero = card }] };
        var text = ReplayFile.WriteBattlegrounds(new ReplayHeader { Summary = new ReplaySummary { Mode = "Battlegrounds" } }, data, ["T 1 30", $"c {card}"]);

        Assert.Equal(kept, ReplayImport.Sanitize(text, CardDb.Empty, out _) is not null);
    }

    [Fact]
    public void Too_many_event_lines_are_refused()
    {
        var text = Constructed(Enumerable.Range(0, ReplayRecorder.MaxEvents + 1).Select(_ => "B"));

        Assert.Null(ReplayImport.Sanitize(text, CardDb.Empty, out _));
    }

    [Fact]
    public void A_packed_file_over_the_limit_is_refused_unread()
    {
        var path = Path.Combine(_dir, "big.hpr");
        using (var file = File.Create(path))
        {
            file.Write([0x1F, 0x8B]);
            file.SetLength(ReplayImport.MaxFileBytes + 1);
        }

        Assert.Equal(ReplayImportError.TooLarge, Import(path).Error);
        Assert.Empty(Imported.Names());
    }

    [Fact]
    public void A_gzip_bomb_stops_at_the_text_limit()
    {
        var path = Path.Combine(_dir, "bomb.hpr");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        {
            gzip.Write(Encoding.ASCII.GetBytes("HPR4\n{\"kind\":\"constructed\"}\n"));
            var chunk = Encoding.ASCII.GetBytes(new string('B', 1024 * 1024));
            for (var i = 0; i < 33; i++)
            {
                gzip.Write(chunk);
            }
        }

        Assert.True(new FileInfo(path).Length < 1024 * 1024);
        Assert.Equal(ReplayImportError.TooLarge, Import(path).Error);
        Assert.Empty(Imported.Names());
    }

    [Fact]
    public void A_replay_file_is_imported_once()
    {
        var path = Path.Combine(_dir, "game.hpr");
        Write(path, Constructed());

        var first = Import(path);
        var again = Import(path);

        Assert.True(first.Ok);
        var name = Assert.Single(first.Added);
        Assert.StartsWith("20261028184107-", name, StringComparison.Ordinal);
        Assert.True(again.Ok);
        Assert.Empty(again.Added);
        Assert.Equal(1, again.Already);
        Assert.DoesNotContain("Legend", Imported.Load(name), StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_file_leaves_nothing_behind()
    {
        var path = Path.Combine(_dir, "bad.hpr");
        Write(path, Constructed([.. ConstructedEvents, "E 9 L PLAY 2 https://evil MINION"]));

        Assert.Equal(ReplayImportError.Damaged, Import(path).Error);
        Assert.Empty(Imported.Names());
        Assert.Equal(ReplayImportError.NotReplay, Import(WriteText("notes.txt", "just some text")).Error);
        Assert.Equal(ReplayImportError.Unreadable, Import(Path.Combine(_dir, "missing.hpr")).Error);
    }

    [Fact]
    public void A_power_log_goes_to_imported_only_through_the_same_filter()
    {
        // A whole short Battlegrounds game (out in 5th) as the client logs it.
        var log = WriteText("Power.log", string.Join('\n', BattlegroundsReplayTests.Game5th().Select(b => "D 12:30:00.0000000 GameState." + b)));
        var history = Path.Combine(Core.AppPaths.Root, "history.db");
        var before = File.Exists(history) ? new FileInfo(history).Length : -1;

        var result = Import(log);

        Assert.True(result.Ok, result.Error.ToString());
        Assert.True(result.FromLog);
        var name = Assert.Single(result.Added);
        var (header, body) = ReplayFile.Read(Imported.Load(name)!)!.Value;
        Assert.Equal(ReplayFile.Battlegrounds, header.Kind);
        Assert.Equal(5, header.Summary!.Placement);
        Assert.Null(header.Summary.RatingBefore);
        Assert.True(header.Events > 5);
        BattlegroundsReplayTests.AssertSeenOnly(body.Take(header.Events).ToList());
        var share = ReplayShare.Prepare(Imported.Load(name)!, Path.Combine(_dir, "share"), CardDb.Empty)!;
        AssertNoAccount(Imported.Load(name)!, ReplayImportTests.ReadPacked(share));
        Assert.Equal(before, File.Exists(history) ? new FileInfo(history).Length : -1);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_dir, "work")));
        Assert.Equal(1, Import(log).Already);
        Assert.Equal(ReplayImportError.None, Import(WriteText("empty.log", "D 12:30:00.0000000 GameState.DebugPrintPower() - CREATE_GAME")).Error);
    }

    [Fact]
    public void A_constructed_power_log_goes_to_imported_without_names_or_account()
    {
        string[] bodies =
        [
            .. ReplayRecorderTests.Setup,
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=PLAYSTATE value=WON ",
            "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE ",
        ];
        var log = WriteText("Power.log", string.Join('\n', bodies.Select(b => "D 12:30:00.0000000 GameState." + b)));

        var result = Import(log);

        Assert.True(result.Ok, result.Error.ToString());
        var name = Assert.Single(result.Added);
        var text = Imported.Load(name)!;
        Assert.Equal(ReplayFile.Constructed, ReplayFile.Read(text)!.Value.Header.Kind);
        AssertNoAccount(text, ReplayImportTests.ReadPacked(ReplayShare.Prepare(text, Path.Combine(_dir, "share"), CardDb.Empty)!));
    }

    /// <summary>No player name, BattleTag or game account id of the log in an imported game or its shared copy.</summary>
    private static void AssertNoAccount(params string[] texts)
    {
        foreach (var text in texts)
        {
            foreach (var personal in new[] { "Me#1234", "Rival#4321", "velox", "21276", "UNKNOWN HUMAN", "GameAccountId", "144115198130930503", "hi=" })
            {
                Assert.DoesNotContain(personal, text, StringComparison.Ordinal);
            }
        }
    }

    private ReplayStore Imported => new(Path.Combine(_dir, "imported"));

    private ReplayImportResult Import(string path)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "work"));
        return ReplayImport.ImportFile(path, Imported, CardDb.Empty, Path.Combine(_dir, "work"));
    }

    private string WriteText(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    internal static string ReadPacked(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    internal static void Write(string path, string text)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        gzip.Write(Encoding.UTF8.GetBytes(text));
    }
}
