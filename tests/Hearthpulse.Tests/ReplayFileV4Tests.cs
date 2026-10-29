using System.Text.Json;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;
using static Hearthpulse.Tests.BattlegroundsReplayTests;

namespace Hearthpulse.Tests;

/// <summary>
/// Format 4: <c>HPR4</c>, the game's summary in the header, Battlegrounds events then the <c>G</c> line; Constructed
/// events and post-game part as in format 3. Files of formats 3 and 2 still open, format 1 never.
/// </summary>
public sealed class ReplayFileV4Tests : IDisposable
{
    private readonly ReplayStore _store = new(Path.Combine(Path.GetTempPath(), $"hp-replay-v4-{Guid.NewGuid():N}"));

    public void Dispose()
    {
        if (Directory.Exists(_store.Directory))
        {
            Directory.Delete(_store.Directory, recursive: true);
        }
    }

    private static readonly ReplaySummary Summary = new()
    {
        Mode = "Battlegrounds",
        GameType = "GT_BATTLEGROUNDS",
        StartedAt = new DateTime(2026, 10, 28, 9, 40, 0, DateTimeKind.Utc),
        Seconds = 1320,
        HeroCardId = "BG36_HERO_000",
        PlayerClass = "NEUTRAL",
        Result = "Win",
        Turns = 12,
        Placement = 4,
        RatingBefore = 6100,
        RatingAfter = 6140,
        TavernTier = 5,
        Tribes = ["MURLOC", "DEMON"],
        HeroPowers = ["BG36_HERO_000p"],
        Trinkets = ["BG36_MagicItem_206"],
        FinalBoard = [new ReplayBgMinion("BG36_110", 12, 14, true, ["TAUNT", "DIVINE_SHIELD"])],
        FinalHand = ["BG28_810"],
    };

    private static GameRecord Record(string mode = "Battlegrounds") => new()
    {
        Key = "20261028094000|me#1234|GT_BATTLEGROUNDS",
        StartedAt = new DateTime(2026, 10, 28, 12, 40, 0),
        EndedAt = new DateTime(2026, 10, 28, 13, 2, 0),
        Mode = mode,
        GameType = mode == "Battlegrounds" ? "GT_BATTLEGROUNDS" : "GT_RANKED",
        PlayerName = "Me#1234",
        OpponentName = "Rival#4321",
        HeroCardId = mode == "Battlegrounds" ? "TB_BaconShop_HERO_50" : "HERO_09",
        PlayerClass = mode == "Battlegrounds" ? "NEUTRAL" : "PRIEST",
        OpponentHeroCardId = mode == "Battlegrounds" ? string.Empty : "HERO_01",
        OpponentClass = mode == "Battlegrounds" ? string.Empty : "WARRIOR",
        Result = "Loss",
        Placement = mode == "Battlegrounds" ? 5 : 0,
        Turns = 2,
        DeckName = mode == "Battlegrounds" ? null : "My secret ladder deck",
        DeckCode = mode == "Battlegrounds" ? null : "AAECAa0GAA==",
        Races = mode == "Battlegrounds" ? "MURLOC,DEMON" : null,
    };

    [Fact]
    public void A_constructed_file_is_format_4_with_its_summary_and_the_format_3_body()
    {
        string[] events = ["T 1 L 0", "E 1 L PLAY 1 CS2_182 MINION ATK=4 HEALTH=5", "R WON", "N 2 64", "K 64 CS2_029 3 played 0"];
        var text = ReplayFile.Write(new ReplayHeader { LocalRank = "Gold 5", Summary = Summary with { Mode = "Ranked" }, Events = 3 }, events);

        Assert.StartsWith("HPR4\n{", text, StringComparison.Ordinal);
        var (header, body) = ReplayFile.Read(text)!.Value;
        Assert.Equal(4, header.Version);
        Assert.Equal("Gold 5", header.LocalRank);
        Assert.Equal(Summary with { Mode = "Ranked" }, header.Summary, SummaryComparer.Instance);
        Assert.Equal(events, body);
        var replay = MatchReplay.Parse(text)!;
        Assert.True(replay.HasPostGame);
        Assert.Equal("WON", replay.Result);
    }

    [Fact]
    public void A_battlegrounds_file_holds_its_steps_then_the_rounds_and_lobby()
    {
        var data = new BattlegroundsReplayData
        {
            Rounds = [new BattlegroundsRound { Turn = 1, Tier = 1, Result = "Win", DamageDealt = 3 }],
            Lobby = [new BattlegroundsLobbyHero("BG36_HERO_000", 4, true)],
        };
        string[] events = ["T 1 40", "E 1 L PLAY 0 BG36_HERO_000 HERO HEALTH=30", "R 4"];
        var text = ReplayFile.WriteBattlegrounds(new ReplayHeader { Summary = Summary }, data, events);

        var (header, body) = ReplayFile.Read(text)!.Value;
        Assert.Equal((ReplayFile.Battlegrounds, 3, 4), (header.Kind, header.Events, header.Version));
        Assert.Equal(events.Length + 1, body.Count);
        Assert.StartsWith("G {", body[^1], StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(ReplayFile.ReadBattlegrounds(body)!.Rounds).Turn);
        Assert.Equal(Summary, header.Summary, SummaryComparer.Instance);
        var steps = BattlegroundsMatchReplay.From(header, body)!;
        Assert.Equal([MatchStepKind.TurnStart, MatchStepKind.End], steps.Frames.Select(f => f.Kind));
        Assert.Equal(4, steps.Frames[^1].Place);
    }

    [Fact]
    public void A_format_3_constructed_file_still_opens_on_the_match_page()
    {
        const string text = "HPR3\n{\"kind\":\"constructed\",\"localRank\":\"Gold 5\",\"truncated\":false,\"events\":4}\n"
                            + "T 1 L 0\nS MAIN_READY\nE 1 L PLAY 1 CS2_182 MINION ATK=4 HEALTH=5\nR WON\nN 2 64\nK 64 CS2_029 3 played 0\n";
        Assert.Equal(3, ReplayFile.Read(text)!.Value.Header.Version);
        var game = Record("Ranked");
        var page = MatchPage.Load(game with { Replay = _store.Save(game.Key, text) }, _store, CardDb.Empty, null);

        Assert.Null(page.Missing);
        Assert.NotNull(page.Replay);
        Assert.True(page.FrameCount > 0);
        Assert.False(page.HasBattlegroundsSteps);
        Assert.Null(page.BattlegroundsStep(0));
    }

    [Fact]
    public void A_format_3_battlegrounds_file_still_opens_with_rounds_and_lobby_but_no_steps()
    {
        const string text = "HPR3\n{\"kind\":\"battlegrounds\",\"truncated\":false,\"events\":0}\n"
                            + "{\"rounds\":[{\"turn\":4,\"tier\":3,\"health\":28,\"board\":[{\"cardId\":\"BG_X\",\"attack\":4,\"health\":5,\"golden\":false}],"
                            + "\"shop\":[],\"result\":\"Loss\",\"damageTaken\":6,\"damageDealt\":0}],"
                            + "\"lobby\":[{\"heroCardId\":\"TB_BaconShop_HERO_08\",\"place\":3,\"local\":true}]}\n";
        var game = Record();
        var page = MatchPage.Load(game with { Replay = _store.Save(game.Key, text) }, _store, CardDb.Empty, null);

        Assert.Null(page.Missing);
        Assert.Equal(4, Assert.Single(page.Rounds).Turn);
        Assert.Single(page.Lobby);
        Assert.False(page.HasBattlegroundsSteps);
        Assert.Equal(0, page.FrameCount);
        Assert.Null(page.BattlegroundsStep(0));
    }

    [Fact]
    public void Format_2_is_still_read_and_format_1_never()
    {
        Assert.Equal(2, ReplayFile.Read("HPR2\n{\"kind\":\"constructed\",\"events\":1}\nT 1 L 0\n")!.Value.Header.Version);
        Assert.Null(ReplayFile.Read("HPR1\n{\"kind\":\"constructed\",\"events\":1}\nT 1 L 0\n"));
    }

    [Fact]
    public void A_battlegrounds_game_is_saved_with_its_steps_and_a_summary_without_names()
    {
        var (game, recorder, tracker) = Play(Game5th());
        var record = Record();
        var saved = ReplayCapture.Attach(record, game, recorder, tracker, _store);
        var text = _store.Load(saved.Replay)!;

        Assert.StartsWith("HPR4\n", text, StringComparison.Ordinal);
        var (header, body) = ReplayFile.Read(text)!.Value;
        Assert.Equal(ReplayFile.Battlegrounds, header.Kind);
        Assert.Equal("R 5", body[header.Events - 1]);
        var summary = header.Summary!;
        Assert.Equal(("TB_BaconShop_HERO_50", 5, "Loss"), (summary.HeroCardId, summary.Placement, summary.Result));
        Assert.Equal(["TB_BaconShop_HP_050"], summary.HeroPowers);
        Assert.Equal(["BGS_039"], summary.FinalHand);
        Assert.Equal(["MURLOC", "DEMON"], summary.Tribes);
        Assert.Equal(DateTimeKind.Utc, summary.StartedAt.Kind);
        Assert.Equal(1320, summary.Seconds);
        foreach (var name in new[] { "Me#1234", "Rival", "#" })
        {
            Assert.DoesNotContain(name, text);
        }
    }

    [Fact]
    public void A_constructed_summary_keeps_the_deck_code_and_drops_names_and_the_deck_name()
    {
        var json = JsonSerializer.Serialize(ReplayCapture.Summary(Record("Ranked"), null, null), ReplayFile.Json);

        Assert.Contains("\"deckCode\":\"AAECAa0GAA==\"", json);
        Assert.Contains("\"opponentClass\":\"WARRIOR\"", json);
        Assert.DoesNotContain("Me#1234", json);
        Assert.DoesNotContain("Rival", json);
        Assert.DoesNotContain("secret", json);
    }

    /// <summary>Records with lists compare by reference; this compares a summary by its JSON.</summary>
    private sealed class SummaryComparer : IEqualityComparer<ReplaySummary?>
    {
        public static readonly SummaryComparer Instance = new();

        public bool Equals(ReplaySummary? x, ReplaySummary? y) =>
            JsonSerializer.Serialize(x, ReplayFile.Json) == JsonSerializer.Serialize(y, ReplayFile.Json);

        public int GetHashCode(ReplaySummary? obj) => 0;
    }
}
