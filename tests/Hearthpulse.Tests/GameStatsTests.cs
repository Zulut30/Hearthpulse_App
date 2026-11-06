using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.GameStats;
using Hearthpulse.Core.Pool;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Tests;

/// <summary>Game records (docs/game-stats.md): what one holds, the queue, and the client against a fake pool.</summary>
public sealed class GameStatsTests : IDisposable
{
    private const string Endpoint = "https://pool.test";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hp-gamestats-" + Guid.NewGuid().ToString("N"));

    public GameStatsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static CardDb Cards() => CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes("""
        [
          {"id":"EX1_015","dbfId":1001,"type":"MINION","cost":1,"collectible":true},
          {"id":"CS2_029","dbfId":1002,"type":"SPELL","cost":4,"collectible":true},
          {"id":"CS2_032","dbfId":1003,"type":"SPELL","cost":7,"collectible":true},
          {"id":"CS2_025","dbfId":1004,"type":"SPELL","cost":2,"collectible":true},
          {"id":"CS2_024","dbfId":1005,"type":"SPELL","cost":2,"collectible":true},
          {"id":"DISC_SRC","dbfId":1006,"type":"SPELL","cost":1,"collectible":true,"mechanics":["DISCOVER"]},
          {"id":"HERO_08","dbfId":637,"type":"HERO","cardClass":"MAGE","collectible":true}
        ]
        """)));

    private static string DeckCode(CardDb cards) =>
        DeckString.Encode(new Dictionary<string, int> { ["EX1_015"] = 2, ["CS2_029"] = 2, ["CS2_032"] = 2, ["CS2_025"] = 2, ["CS2_024"] = 2, ["DISC_SRC"] = 2 }, "MAGE", "Standard", cards)!;

    /// <summary>
    /// A small Constructed game: a mulligan (keep two, replace one), a 1-mana play, a Discover pick, a created card
    /// played, an Excavate treasure in hand, and an opponent secret that the game never showed (named only post-game).
    /// </summary>
    private static string Replay(bool postGame = true)
    {
        string[] body =
        [
            "T 1 L 0",
            "E 1 L HAND 1 CS2_029 SPELL COST=4",
            "E 2 L HAND 2 CS2_032 SPELL COST=7",
            "E 3 L HAND 3 EX1_015 MINION COST=1",
            "E 9 O HAND 1 - SPELL",
            "S BEGIN_MULLIGAN",
            "C MULLIGAN - CS2_029,CS2_032,EX1_015",
            "V L MULLIGAN_STATE DEALING",
            "X 2",
            "E 4 L HAND 2 CS2_025 SPELL COST=2",
            "V L MULLIGAN_STATE DONE",
            "S MAIN_READY",
            "T 3 L 30",
            "E 5 L HAND 4 DISC_SRC SPELL COST=1",
            "V L RESOURCES 2",
            "V L RESOURCES_USED 0",
            "P 3 -",
            "E 3 L PLAY 1 EX1_015 MINION ATK=1 HEALTH=1",
            "V L RESOURCES_USED 1",
            "P 5 -",
            "E 5 L GRAVEYARD 1 DISC_SRC SPELL",
            "C 1 DISC_SRC CS2_031,GVG_001,OG_090",
            "c GVG_001",
            "E 6 L HAND 4 GVG_001 SPELL COST=1",
            "V L RESOURCES_USED 2",
            "E 7 L HAND 5 WW_001t SPELL COST=1",
            "T 4 O 60",
            "P 9 -",
            "E 9 O SECRET 1 - SPELL",
            "T 5 L 90",
            "V L RESOURCES 3",
            "V L RESOURCES_USED 0",
            "P 6 -",
            "E 6 L GRAVEYARD 1 GVG_001 SPELL",
            "V L RESOURCES_USED 1",
            "R WON",
            .. postGame ? (string[])["N 9 90", "K 90 EX1_130 4 played 0"] : [],
        ];
        return ReplayFile.Write(new ReplayHeader(), body);
    }

    private static GameRecord Ranked(string? deck) => new()
    {
        Key = "20261105120000|Me#1234|GT_RANKED",
        StartedAt = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc),
        EndedAt = new DateTime(2026, 11, 5, 12, 9, 0, DateTimeKind.Utc),
        Mode = "Ranked",
        GameType = "GT_RANKED",
        Format = "FT_STANDARD",
        PlayerName = "Me#1234",
        OpponentName = "Them#5678",
        HeroCardId = "HERO_08",
        PlayerClass = "MAGE",
        OpponentClass = "ROGUE",
        Result = "Win",
        Turns = 5,
        Rank = "Legend 1,204",
        WentFirst = true,
        DeckCode = deck,
        DeckName = "My secret list",
        OpponentArchetype = "Secret Rogue",
    };

    [Fact]
    public void A_constructed_record_holds_the_game_as_the_player_saw_it()
    {
        var cards = Cards();
        var deck = DeckCode(cards);
        var record = GameStatsRecord.Build(Ranked(deck), new string('a', 32), Replay(), null, cards, "0.18.0")!;

        Assert.Equal("ranked", (string?)record["mode"]);
        Assert.Equal("standard", (string?)record["fmt"]);
        Assert.Equal("legend", (string?)record["rank"]);
        Assert.Equal("W", (string?)record["res"]);
        Assert.Equal(540, (int)record["secs"]!);
        Assert.Equal(20762, (int)record["day"]!);
        Assert.True((bool)record["first"]!);
        Assert.Equal(("MAGE", "ROGUE", "Secret Rogue"), ((string?)record["cls"], (string?)record["ocls"], (string?)record["oarch"]));
        Assert.Equal(deck, (string?)record["deck"]);

        var mulligan = record["mull"]!;
        Assert.Equal(["CS2_029", "CS2_032", "EX1_015"], Strings(mulligan["o"]));
        Assert.Equal(["CS2_029", "EX1_015"], Strings(mulligan["k"]));
        Assert.Equal(["CS2_032"], Strings(mulligan["r"]));
        Assert.Equal(["CS2_025"], Strings(mulligan["n"]));

        // [turn, card, mana spent, created]: the created GVG_001 is not in the deck list.
        Assert.Equal("[[2,\"EX1_015\",1,0],[2,\"DISC_SRC\",0,0],[3,\"GVG_001\",1,1]]", record["plays"]!.ToJsonString());
        Assert.Equal("[[2,\"discover\",\"DISC_SRC\",[\"CS2_031\",\"GVG_001\",\"OG_090\"],[\"GVG_001\"]]]", record["choices"]!.ToJsonString());
        Assert.Equal("[[2,\"WW_001t\"]]", record["exc"]!.ToJsonString());
        Assert.Contains("[2,\"DISC_SRC\",0]", record["draws"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("[2,\"GVG_001\",1]", record["draws"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_never_holds_names_the_deck_name_the_legend_number_or_what_the_game_did_not_show()
    {
        var cards = Cards();
        var json = GameStatsRecord.Build(Ranked(DeckCode(cards)), new string('a', 32), Replay(), null, cards, null)!.ToJsonString();

        foreach (var hidden in new[] { "Me#1234", "Them#5678", "My secret list", "1,204", "1204", "EX1_130" })
        {
            Assert.DoesNotContain(hidden, json, StringComparison.Ordinal);
        }

        // The opponent's secret was played face down and never shown: no name in the opponent's plays.
        Assert.Equal("[]", JsonNode.Parse(json)!["oplays"]!.ToJsonString());
    }

    [Fact]
    public void Without_a_deck_list_no_card_counts_as_created()
    {
        var record = GameStatsRecord.Build(Ranked(null), new string('a', 32), Replay(), null, Cards(), null)!;
        Assert.Null(record["deck"]);
        Assert.DoesNotContain(",1]", record["plays"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Friendly", "Win")]
    [InlineData("Practice", "Win")]
    [InlineData("Mercenaries", "Win")]
    [InlineData("Ranked", "Abandoned")]
    [InlineData("Ranked", "Unknown")]
    public void Other_modes_and_games_without_a_result_are_not_sent(string mode, string result) =>
        Assert.Null(GameStatsRecord.Build(Ranked(null) with { Mode = mode, Result = result }, new string('a', 32), null, null, null, null));

    [Fact]
    public void A_battlegrounds_record_has_the_hero_pick_lobby_and_rating_bucket()
    {
        var game = new GameRecord
        {
            Key = "20261105120000|Me#1234|GT_BATTLEGROUNDS",
            StartedAt = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc),
            EndedAt = new DateTime(2026, 11, 5, 12, 20, 0, DateTimeKind.Utc),
            Mode = "Battlegrounds",
            Result = "Win",
            Placement = 3,
            RatingBefore = 6789,
            Turns = 12,
            HeroCardId = "BG_HERO_1",
        };
        var details = new PoolDetails(12, 5, 1, "BG_ANOMALY_1", ["BEAST", "DRAGON"], "BEAST", [new PoolMinion("BG_MINION_1", true, 10, 12)],
            ["T_A", "T_B"], ["T_A"], [new TierStep(3, 2)], [new PoolCombat(4, "W", 0, 5, 0.5)])
        {
            Choices = [new BgChoice(5, "quest", null, ["Q_A", "Q_B"], ["Q_A"])],
        };
        var pool = new PoolGame(new string('b', 32), "solo", 6500, ["BG_HERO_1", "BG_HERO_2"], "BG_HERO_1", 3) { Details = details };

        var record = GameStatsRecord.Build(game, new string('c', 32), null, pool, null, null)!;
        var bg = record["bg"]!;

        Assert.Equal("bg", (string?)record["mode"]);
        Assert.Equal(3, (int)bg["placement"]!);
        Assert.Equal(6500, (int)bg["mmr"]!);
        Assert.Equal("BG_HERO_1", (string?)bg["picked"]);
        Assert.Equal(["BG_HERO_1", "BG_HERO_2"], Strings(bg["offered"]));
        Assert.Equal(["BEAST", "DRAGON"], Strings(bg["tribes"]));
        Assert.Equal(["Q_A"], Strings(bg["quests"]));
        Assert.Equal("BG_ANOMALY_1", (string?)bg["anomaly"]);
        Assert.Equal("[[3,2]]", bg["tierups"]!.ToJsonString());
        Assert.Null(record["deck"]);
    }

    [Fact]
    public void Ids_are_keyed_hashes_stable_per_install()
    {
        var settings = new AppSettings { PoolSecret = new string('5', 64) };
        var id = GameStatsClient.RecordId("k1", settings);
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal(id, GameStatsClient.RecordId("k1", settings));
        Assert.NotEqual(id, GameStatsClient.RecordId("k2", settings));
        Assert.NotEqual(id, GameStatsClient.InstallId(settings));
        Assert.NotEqual(GameStatsClient.InstallId(settings), GameStatsClient.InstallId(new AppSettings { PoolSecret = new string('6', 64) }));
    }

    [Fact]
    public void Sharing_needs_the_consent_and_an_https_pool()
    {
        Assert.False(GameStatsClient.Sharing(new AppSettings { PoolEndpoint = Endpoint }));
        Assert.False(GameStatsClient.Sharing(new AppSettings { PoolEndpoint = "http://pool.test", GameStatsConsent = 1 }));
        Assert.True(GameStatsClient.Sharing(new AppSettings { PoolEndpoint = Endpoint, GameStatsConsent = 1 }));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, GameStatsAnswer.Stored)]
    [InlineData(HttpStatusCode.BadRequest, GameStatsAnswer.Refused)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, GameStatsAnswer.Refused)]
    [InlineData(HttpStatusCode.TooManyRequests, GameStatsAnswer.Later)]
    [InlineData(HttpStatusCode.ServiceUnavailable, GameStatsAnswer.Later)]
    [InlineData(HttpStatusCode.NotFound, GameStatsAnswer.Later)]
    public async Task The_client_posts_one_batch_and_reads_the_answer(HttpStatusCode status, GameStatsAnswer expected)
    {
        var pool = new FakePool(status);
        using var http = new HttpClient(pool);
        var answer = await GameStatsClient.SubmitAsync(http, Endpoint, new string('1', 32), [Record('a')]);

        Assert.Equal(expected, answer);
        var (path, body) = Assert.Single(pool.Requests);
        Assert.Equal("/v1/events", path);
        var json = JsonNode.Parse(body)!;
        Assert.Equal(1, (int)json["v"]!);
        Assert.Equal(new string('1', 32), (string?)json["iid"]);
        Assert.Single(json["games"]!.AsArray());
    }

    [Fact]
    public async Task Offline_is_a_later_not_a_refusal()
    {
        using var http = new HttpClient(new FakePool(HttpStatusCode.OK) { Fail = true });
        Assert.Equal(GameStatsAnswer.Later, await GameStatsClient.SubmitAsync(http, Endpoint, new string('1', 32), [Record('a')]));
        Assert.False(await GameStatsClient.ForgetAsync(http, Endpoint, new string('1', 32)));
    }

    [Fact]
    public void The_queue_keeps_one_file_per_record_and_drops_old_damaged_and_extra_ones()
    {
        var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        var queue = new GameStatsQueue(_folder) { UtcNow = () => now };
        queue.Add(Record('a'));
        queue.Add(Record('a'));
        queue.Add(new JsonObject { ["id"] = "../../evil" });
        Assert.Equal(1, queue.Count);

        File.WriteAllText(Path.Combine(_folder, new string('d', 32) + ".json"), "{not json");
        Assert.Single(queue.Take(10));
        Assert.Equal(1, queue.Count);

        var old = new GameStatsQueue(_folder) { UtcNow = () => now + GameStatsQueue.MaxAge + TimeSpan.FromDays(1) };
        Assert.Empty(old.Take(10));

        for (var i = 0; i < GameStatsQueue.MaxRecords + 5; i++)
        {
            queue.Add(Record(i));
        }

        Assert.Equal(GameStatsQueue.MaxRecords, queue.Count);
    }

    [Fact]
    public async Task The_uploader_sends_in_batches_and_keeps_what_the_pool_did_not_take()
    {
        var settings = new AppSettings { PoolEndpoint = Endpoint, GameStatsConsent = GameStatsClient.ConsentVersion, PoolSecret = new string('5', 64) };
        var queue = new GameStatsQueue(_folder);
        for (var i = 0; i < 25; i++)
        {
            queue.Add(Record(i));
        }

        var pool = new FakePool(HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(pool);
        var history = new HistoryStore(Path.Combine(_folder, "h.db"));
        using var uploader = new GameStatsUploader(settings, http, history, () => null, () => null, null, queue) { Delay = TimeSpan.Zero };

        Assert.False(await uploader.PassAsync(CancellationToken.None));
        Assert.Equal(25, queue.Count);

        pool.Status = HttpStatusCode.OK;
        Assert.True(await uploader.PassAsync(CancellationToken.None));
        Assert.Equal(0, queue.Count);
        Assert.Equal([20, 5], pool.Requests.Skip(1).Select(r => JsonNode.Parse(r.Body)!["games"]!.AsArray().Count));
    }

    [Fact]
    public async Task Withdrawing_drops_the_queue_and_asks_the_pool_to_delete_until_it_answers()
    {
        var settings = new AppSettings { PoolEndpoint = Endpoint, GameStatsConsent = GameStatsClient.ConsentVersion, PoolSecret = new string('5', 64) };
        var queue = new GameStatsQueue(_folder);
        queue.Add(Record('a'));
        var pool = new FakePool(HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(pool);
        var history = new HistoryStore(Path.Combine(_folder, "h.db"));
        using var uploader = new GameStatsUploader(settings, http, history, () => null, () => null, null, queue) { Delay = TimeSpan.Zero };

        settings.GameStatsConsent = 0;
        uploader.Withdraw();
        Assert.Equal(0, queue.Count);
        Assert.False(await uploader.PassAsync(CancellationToken.None));
        Assert.True(queue.ForgetPending);

        pool.Status = HttpStatusCode.OK;
        Assert.True(await uploader.PassAsync(CancellationToken.None));
        Assert.False(queue.ForgetPending);
        Assert.All(pool.Requests, r => Assert.Equal("/v1/events/forget", r.Path));
        Assert.Equal(GameStatsClient.InstallId(settings), (string?)JsonNode.Parse(pool.Requests[^1].Body)!["iid"]);
    }

    [Fact]
    public void A_game_ending_while_sharing_is_off_is_not_queued()
    {
        var settings = new AppSettings { PoolEndpoint = Endpoint };
        var queue = new GameStatsQueue(_folder);
        using var http = new HttpClient(new FakePool(HttpStatusCode.OK));
        var history = new HistoryStore(Path.Combine(_folder, "h.db"));
        using var uploader = new GameStatsUploader(settings, http, history, () => null, () => null, null, queue);
        uploader.OnGameFinished(new GameOutcome("Ranked", "Win", 0, null, null, "HERO_08") { Key = "k" });
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void The_uploader_builds_a_record_from_the_history_row_and_its_replay()
    {
        var settings = new AppSettings { PoolEndpoint = Endpoint, GameStatsConsent = GameStatsClient.ConsentVersion, PoolSecret = new string('5', 64) };
        var replays = new ReplayStore(Path.Combine(_folder, "replays"));
        var game = Ranked(null);
        var name = replays.Save(game.Key, Replay())!;
        var history = new HistoryStore(Path.Combine(_folder, "h.db"));
        history.Add(game with { Replay = name });
        using var http = new HttpClient(new FakePool(HttpStatusCode.OK));
        using var uploader = new GameStatsUploader(settings, http, history, () => replays, Cards, "0.18.0", new GameStatsQueue(_folder));

        var record = uploader.Build(new GameOutcome("Ranked", "Win", 0, null, null, "HERO_08") { Key = game.Key })!;

        Assert.Equal(GameStatsClient.RecordId(game.Key, settings), (string?)record["id"]);
        Assert.Equal("0.18.0", (string?)record["app"]);
        Assert.Equal(3, record["plays"]!.AsArray().Count);
    }

    private static JsonObject Record(char c) => new() { ["id"] = new string(c, 32), ["mode"] = "ranked", ["res"] = "W" };

    private static JsonObject Record(int i) => new() { ["id"] = i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture), ["mode"] = "ranked", ["res"] = "W" };

    private static string[] Strings(JsonNode? node) => node!.AsArray().Select(n => (string)n!).ToArray();

    private sealed class FakePool(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;

        public bool Fail { get; init; }

        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new HttpRequestException("offline");
            }

            Requests.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Status) { Content = new StringContent("{}") };
        }
    }
}
